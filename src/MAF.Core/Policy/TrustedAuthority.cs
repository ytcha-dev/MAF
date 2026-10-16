namespace MAF.Core.Policy;

// Supplied only by trusted application composition, never deserialized from an operation/CLI.
// Implementations must authenticate provenance (e.g. protected deployment configuration or a
// future authenticated broker). SourceId/digest comparisons alone are NOT proof of provenance.
public sealed record AcquiredPolicy(string SourceId, PolicySnapshot Snapshot, string Digest);
public interface ITrustedPolicyAcquisition
{
    Task<AcquiredPolicy?> AcquireAsync(CancellationToken token);
}

public enum IdentityKind { Unknown, Agent, HumanOperator }
public sealed record AuthenticatedIdentity(string SubjectId, string AuthenticationContextId,
    IdentityKind Kind, DateTimeOffset NotBefore, DateTimeOffset NotAfter, bool FreshHumanPresence);

// Resolves/verifies transport-owned context. Merely implementing ITrustedCallerContext or
// matching its strings must not authenticate a caller. No OS SID / prompt / test fallback.
public interface ITrustedIdentityResolver
{
    AuthenticatedIdentity? Resolve(ITrustedCallerContext context);
}

// Authentication and exact operation authorization are separate trusted responsibilities.
// Implementations must not infer Decide from read/list rights or accept model claims.
public interface IApprovalAuthorization
{
    bool CanDecide(AuthenticatedIdentity identity, PolicyRequest operation, string bindingDigest,
        string requiredOperatorScope, PolicySnapshot currentPolicy);
    bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest operation, string bindingDigest,
        string requiredOperatorScope, PolicySnapshot currentPolicy);
    bool CanControl(AuthenticatedIdentity identity, PolicyRequest operation, PolicySnapshot currentPolicy);
}
public interface IExecutionBindingVerifier
{
    bool Matches(PolicyRequest operation, DurableExecution execution);
}

// No trust integration is installed by M2-B. An absent integration fails closed, never falls
// back to same-account SID, usernames or synthetic identities. Nothing is registered in Host.
public sealed class UnavailableTrust : ITrustedPolicyAcquisition, ITrustedIdentityResolver,
    IApprovalAuthorization, IExecutionBindingVerifier
{
    public static UnavailableTrust Instance { get; } = new();
    private UnavailableTrust() { }
    public Task<AcquiredPolicy?> AcquireAsync(CancellationToken token) => Task.FromResult<AcquiredPolicy?>(null);
    public AuthenticatedIdentity? Resolve(ITrustedCallerContext context) => null;
    public bool CanDecide(AuthenticatedIdentity identity, PolicyRequest operation, string bindingDigest,
        string requiredOperatorScope, PolicySnapshot currentPolicy) => false;
    public bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest operation, string bindingDigest,
        string requiredOperatorScope, PolicySnapshot currentPolicy) => false;
    public bool CanControl(AuthenticatedIdentity identity, PolicyRequest operation, PolicySnapshot currentPolicy) => false;
    public bool Matches(PolicyRequest operation, DurableExecution execution) => false;
}

// Policy acquisition is serialized with all approval/control mutations through commit. The
// SQLite coordinator remains the durable activation/high-water owner across instances/processes.
// This adapter alone is NOT a persistent authority, broker, authenticator or dispatch grant.
public sealed class TrustedPolicyAuthority : IProtectedPolicyAuthority
{
    readonly ITrustedPolicyAcquisition acquisition;
    readonly string sourceId, authorityId;
    readonly TimeProvider clock;
    readonly ITrustedIdentityResolver identities;
    readonly IApprovalAuthorization authorization;
    readonly IExecutionBindingVerifier executionBinding;
    readonly SemaphoreSlim gate = new(1, 1);
    PolicySnapshot? current;
    PolicySnapshot? highest;
    DateTimeOffset lastObserved;

    public TrustedPolicyAuthority(string sourceId, string authorityId, ITrustedPolicyAcquisition acquisition,
        TimeProvider clock, ITrustedIdentityResolver? identities = null, IApprovalAuthorization? authorization = null,
        IExecutionBindingVerifier? executionBinding = null)
    {
        if (!PolicyValidation.Text(sourceId) || !PolicyValidation.Text(authorityId)) throw new ArgumentException("InvalidAuthorityIdentity");
        this.sourceId = sourceId; this.authorityId = authorityId;
        this.acquisition = acquisition ?? throw new ArgumentNullException(nameof(acquisition));
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        this.identities = identities ?? UnavailableTrust.Instance;
        this.authorization = authorization ?? UnavailableTrust.Instance;
        this.executionBinding = executionBinding ?? UnavailableTrust.Instance;
    }

    public async ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken token = default)
    {
        if (!await gate.WaitAsync(TimeSpan.FromSeconds(5), token)) throw new InvalidOperationException("AuthorityBusy");
        try
        {
            current = null; // Failed refresh never leaves an old policy available as fallback.
            AcquiredPolicy? acquired;
            try { acquired = await acquisition.AcquireAsync(token).WaitAsync(TimeSpan.FromSeconds(5), token); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { throw new InvalidOperationException("AuthorityUnavailable"); }
            if (acquired is null) throw new InvalidOperationException("PolicyUnavailable");
            var p = acquired.Snapshot;
            if (acquired.SourceId != sourceId || p.AuthorityId != authorityId) throw new InvalidOperationException("AuthorityMismatch");
            DateTimeOffset now;
            try { now = clock.GetUtcNow(); }
            catch (Exception) { throw new InvalidOperationException("AuthorityUnavailable"); }
            if (now < lastObserved || !DeterministicPolicyEvaluator.ValidSnapshot(p, now)) throw new InvalidOperationException("InvalidAuthorityPolicy");
            var digest = PolicySnapshotBinding.Digest(p);
            if (digest != acquired.Digest) throw new InvalidOperationException("AuthorityDigestMismatch");
            if (highest is not null)
            {
                if (p.Epoch.Value < highest.Epoch.Value || p.Version.Value < highest.Version.Value) throw new InvalidOperationException("AuthorityRollback");
                if (!(p.Epoch.Value > highest.Epoch.Value && p.Version.Value > highest.Version.Value)
                    && (p.Epoch != highest.Epoch || p.Version != highest.Version || digest != PolicySnapshotBinding.Digest(highest)))
                    throw new InvalidOperationException("AuthorityRevisionConflict");
            }
            highest = current = p; lastObserved = now;
            return new Lease(this);
        }
        catch (OperationCanceledException) { gate.Release(); throw; }
        catch (InvalidOperationException) { gate.Release(); throw; }
        catch (Exception) { gate.Release(); throw new InvalidOperationException("AuthorityUnavailable"); }
    }
    sealed class Lease(TrustedPolicyAuthority owner) : IAsyncDisposable
    {
        TrustedPolicyAuthority? held = owner;
        public ValueTask DisposeAsync()
        {
            var released = Interlocked.Exchange(ref held, null);
            if (released is not null) { released.current = null; released.gate.Release(); }
            return ValueTask.CompletedTask;
        }
    }
    public PolicySnapshot? GetSnapshot() => current;
    bool Safe(Func<bool> check) { try { return current is not null && clock.GetUtcNow() >= lastObserved && DeterministicPolicyEvaluator.ValidSnapshot(current, clock.GetUtcNow()) && check(); } catch (Exception) { return false; } }
    AuthenticatedIdentity? Resolve(ITrustedCallerContext context)
    {
        var i = identities.Resolve(context); var now = clock.GetUtcNow();
        return i is not null && PolicyValidation.Text(i.SubjectId) && PolicyValidation.Text(i.AuthenticationContextId)
            && i.SubjectId == context.SubjectId && i.AuthenticationContextId == context.AuthenticationContextId
            && Enum.IsDefined(i.Kind) && i.Kind != IdentityKind.Unknown && now >= i.NotBefore && now < i.NotAfter ? i : null;
    }
    string Binding(PolicyRequest op) => OperationBinding.Digest(op,
        current!.Capabilities.Single(d => d.ConnectorId == op.ConnectorId && d.Capability == op.Capability));
    bool Current(PolicyRequest op) => current is not null && op.AuthorityId == current.AuthorityId && op.Epoch == current.Epoch && op.Version == current.Version;
    public PolicyDecision EvaluateStoredOperation(PolicyRequest operation, DateTimeOffset evaluatedAt)
        // The subject is historical data, not a newly authenticated operator. Consumption separately
        // authenticates the actual caller and reauthorizes the recorded decision subject.
        => new DeterministicPolicyEvaluator(new ImmutablePolicySource(current!), new At(evaluatedAt)).Evaluate(operation, new HistoricalSubject(operation.CallerSubject));
    sealed record HistoricalSubject(string SubjectId) : ITrustedCallerContext { public string AuthenticationContextId => "historical-policy-evaluation"; }
    sealed class At(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
    public bool CanDecide(ITrustedCallerContext actor, PolicyRequest operation, string requiredOperatorScope) => Safe(() =>
    {
        var i = Resolve(actor);
        return i is { Kind: IdentityKind.HumanOperator, FreshHumanPresence: true } && i.SubjectId != operation.CallerSubject
            && Current(operation) && PolicyValidation.Text(requiredOperatorScope)
            && authorization.CanDecide(i, operation, Binding(operation), requiredOperatorScope, current!);
    });
    public bool IsDecisionSubjectStillAuthorized(string recordedSubject, PolicyRequest operation, string requiredOperatorScope)
        => Safe(() => PolicyValidation.Text(recordedSubject) && recordedSubject != operation.CallerSubject && Current(operation)
            && authorization.IsDecisionSubjectStillAuthorized(recordedSubject, operation, Binding(operation), requiredOperatorScope, current!));
    public bool CanControl(ITrustedCallerContext actor, PolicyRequest operation) => Safe(() =>
    {
        var i = Resolve(actor);
        return i is not null && i.SubjectId == operation.CallerSubject && Current(operation)
            && authorization.CanControl(i, operation, current!);
    });
    public bool MatchesExecutionBinding(PolicyRequest operation, DurableExecution execution)
        => Safe(() => Current(operation) && executionBinding.Matches(operation, execution));
}
