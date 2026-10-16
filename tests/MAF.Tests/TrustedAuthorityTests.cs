using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace MAF.Tests;

// Synthetic transport/provenance/human-presence assertions exist only in this test assembly.
public sealed class TrustedAuthorityTests : IDisposable
{
    readonly SyntheticClock clock = new();
    readonly Source source = new();
    readonly Trust trust = new();
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-authority-" + Guid.NewGuid().ToString("N"));
    string Db => Path.Combine(root, "synthetic.db");
    sealed class Source : ITrustedPolicyAcquisition
    {
        public AcquiredPolicy? Candidate = Wrap(SyntheticApproval.Policy);
        public Exception? Error;
        public int Reads;
        public Action? OnAcquire;
        public Task<AcquiredPolicy?> AcquireAsync(CancellationToken token)
        { Interlocked.Increment(ref Reads); OnAcquire?.Invoke(); if (Error is not null) throw Error; return Task.FromResult(Candidate); }
    }
    static AcquiredPolicy Wrap(PolicySnapshot p) => new("protected-test-source", p, PolicySnapshotBinding.Digest(p));
    static PolicySnapshot Next(PolicyOutcome outcome = PolicyOutcome.Deny) => SyntheticApproval.Policy with
    { Epoch = new(2), Version = new(2), Rules = [SyntheticApproval.Policy.Rules[0] with { Outcome = outcome }] };
    sealed class Trust : ITrustedIdentityResolver, IApprovalAuthorization, IExecutionBindingVerifier
    {
        public readonly SyntheticActor Caller = new("submitter"), Operator = new("operator");
        public bool Missing, Throw, Rights = true, Human = true, Present = true;
        public TimeSpan Expires = TimeSpan.FromMinutes(1);
        public string Scope = "operator:readonly";
        public AuthenticatedIdentity? Resolve(ITrustedCallerContext context)
        {
            if (Throw) throw new IOException("private synthetic failure");
            if (Missing || !ReferenceEquals(context, Caller) && !ReferenceEquals(context, Operator)) return null;
            var now = new SyntheticClock().Now;
            return new(context.SubjectId, context.AuthenticationContextId,
                ReferenceEquals(context, Operator) && Human ? IdentityKind.HumanOperator : IdentityKind.Agent,
                now.AddMinutes(-1), now + Expires, Present);
        }
        public bool CanDecide(AuthenticatedIdentity identity, PolicyRequest op, string digest, string scope, PolicySnapshot p)
            => Rights && identity.SubjectId == "operator" && scope == Scope && op.Scope == SyntheticApproval.Scope
                && digest == OperationBinding.Digest(op, p.Capabilities[0]);
        public bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest op, string digest, string scope, PolicySnapshot p)
            => Rights && subject == "operator" && scope == Scope && op.Scope == SyntheticApproval.Scope
                && digest == OperationBinding.Digest(op, p.Capabilities[0]);
        public bool CanControl(AuthenticatedIdentity identity, PolicyRequest op, PolicySnapshot p) => Rights && identity.SubjectId == "submitter";
        public bool Matches(PolicyRequest op, DurableExecution e) => e.Handle.Provider == "Synthetic";
    }
    TrustedPolicyAuthority Authority(bool configured = true) => new("protected-test-source", "authority", source, clock,
        configured ? trust : null, configured ? trust : null, configured ? trust : null);
    SqliteApprovalStore Store(TrustedPolicyAuthority a, Action<ApprovalCheckpoint>? fault = null, int capacity = 10000)
        => new(Db, a, clock, capacity, fault);
    async Task Setup() { Directory.CreateDirectory(root); await ApprovalSchema.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance); }
    async Task<ApprovalRecord> Approved(SqliteApprovalStore s)
    {
        var r = await s.RequestAsync("approval-one", SyntheticApproval.Request(), trust.Caller);
        return await s.DecideAsync(new(r.ApprovalId, "decision-one", r.Version, r.BindingDigest, ApprovalChoice.Approve), trust.Operator);
    }
    Task<DurableExecution> Consume(SqliteApprovalStore s, ApprovalRecord r) => s.ConsumeAsync(r.ApprovalId, r.Version, 0,
        r.BindingDigest, SyntheticApproval.Execution(r.Operation, clock.Now), trust.Caller);
    async Task<object?> Sql(string sql)
    {
        await using var c = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Db, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
        await c.OpenAsync(); using var command = c.CreateCommand(); command.CommandText = sql; return await command.ExecuteScalarAsync();
    }

    [Fact]
    public void PersistedM2APolicyDigestEncodingRemainsCompatible()
    {
        // Independent baseline vector: M2-A BinaryWriter v1 encoding, synthetic fixture only.
        Assert.Equal("AE11A91E80130F71BAD8B602BC5110D674D0D27D9D58F6231C6B3D7E2B9E758E", PolicySnapshotBinding.Digest(SyntheticApproval.Policy));
    }
    [Fact]
    public async Task NoInstalledTrustCannotActivateOrApprove()
    {
        var absent = new TrustedPolicyAuthority("deployment-source", "authority", UnavailableTrust.Instance, clock);
        Assert.Equal("PolicyUnavailable", (await Assert.ThrowsAsync<InvalidOperationException>(() => absent.AcquireAsync().AsTask())).Message);
        var a = Authority(false); await using var lease = await a.AcquireAsync();
        Assert.False(a.CanDecide(SyntheticApproval.Operator, SyntheticApproval.Request(), "operator:readonly"));
        Assert.False(a.CanControl(SyntheticApproval.Caller, SyntheticApproval.Request()));
        Assert.False(a.IsDecisionSubjectStillAuthorized("operator", SyntheticApproval.Request(), "operator:readonly"));
    }
    [Fact]
    public async Task ProtectedSourceExactBindingCanPersistButCannotDispatch()
    {
        await Setup(); var s = Store(Authority()); var r = await Approved(s);
        Assert.Equal(ApprovalStatus.Approved, r.Status); Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
        await Consume(s, r);
        Assert.Equal(ApprovalStatus.Consumed, (await s.GetAsync(r.ApprovalId))!.Status);
        Assert.Equal(1L, await Sql("SELECT count(*) FROM executions")); Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Theory]
    [InlineData("source", "AuthorityMismatch")]
    [InlineData("authority", "AuthorityMismatch")]
    [InlineData("digest", "AuthorityDigestMismatch")]
    [InlineData("missing", "PolicyUnavailable")]
    [InlineData("exception", "AuthorityUnavailable")]
    public async Task UntrustedOrUnavailableAcquisitionFailsClosed(string change, string reason)
    {
        var a = Authority(); await using (await a.AcquireAsync()) { Assert.NotNull(a.GetSnapshot()); }
        source.Candidate = change switch
        {
            "source" => source.Candidate! with { SourceId = "repo-claimed-source" },
            "authority" => Wrap(SyntheticApproval.Policy with { AuthorityId = "request-claimed-authority" }),
            "digest" => source.Candidate! with { Digest = new string('0', 64) },
            "missing" => null,
            _ => source.Candidate
        };
        if (change == "exception") source.Error = new IOException("private source details");
        Assert.Equal(reason, (await Assert.ThrowsAsync<InvalidOperationException>(() => a.AcquireAsync().AsTask())).Message);
        Assert.Null(a.GetSnapshot()); Assert.False(a.CanControl(trust.Caller, SyntheticApproval.Request()));
    }
    [Theory]
    [InlineData(1, 1, false, "AuthorityRollback")]
    [InlineData(2, 3, false, "AuthorityRevisionConflict")]
    [InlineData(3, 2, false, "AuthorityRevisionConflict")]
    [InlineData(2, 2, true, "AuthorityRevisionConflict")]
    public async Task RevisionsCannotRollbackOrChangeInPlace(long epoch, long version, bool changed, string reason)
    {
        var a = Authority(); source.Candidate = Wrap(Next()); await using (await a.AcquireAsync()) { }
        var p = Next() with { Epoch = new(epoch), Version = new(version) };
        if (changed) p = p with { DecisionLifetime = TimeSpan.FromMinutes(4) };
        source.Candidate = Wrap(p);
        Assert.Equal(reason, (await Assert.ThrowsAsync<InvalidOperationException>(() => a.AcquireAsync().AsTask())).Message);
    }
    [Fact]
    public async Task ReorderedPolicyIsSameContentAndBothRevisionsCanAdvance()
    {
        var p = SyntheticApproval.Policy with { Rules = [SyntheticApproval.Policy.Rules[0], SyntheticApproval.Policy.Rules[0] with { CallerSubject = "other" }] };
        var reverse = p with { Rules = [p.Rules[1], p.Rules[0]] };
        Assert.Equal(PolicySnapshotBinding.Digest(p), PolicySnapshotBinding.Digest(reverse));
        var a = Authority(); source.Candidate = Wrap(p); await using (await a.AcquireAsync()) { }
        source.Candidate = Wrap(reverse); await using (await a.AcquireAsync()) { }
        source.Candidate = Wrap(Next()); await using (await a.AcquireAsync()) { Assert.Equal(new PolicyEpoch(2), a.GetSnapshot()!.Epoch); }
    }
    [Theory]
    [InlineData("missing")][InlineData("forged")][InlineData("scope")][InlineData("self")]
    [InlineData("agent")][InlineData("no-presence")][InlineData("expired")][InlineData("unavailable")][InlineData("no-rights")]
    public async Task OperatorProofAndExactPermissionAreMandatory(string variant)
    {
        var a = Authority(); await using var lease = await a.AcquireAsync();
        ITrustedCallerContext actor = trust.Operator; var op = SyntheticApproval.Request();
        switch (variant)
        {
            case "missing": trust.Missing = true; break;
            case "forged": actor = new SyntheticActor("operator"); break; // identical strings, different transport context
            case "scope": trust.Scope = "read:list"; break;
            case "self": op = op with { CallerSubject = "operator" }; break;
            case "agent": trust.Human = false; break;
            case "no-presence": trust.Present = false; break;
            case "expired": trust.Expires = TimeSpan.Zero; break;
            case "unavailable": trust.Throw = true; break;
            case "no-rights": trust.Rights = false; break;
        }
        Assert.False(a.CanDecide(actor, op, "operator:readonly"));
    }
    [Fact]
    public async Task ChangedResourceOrPolicyCannotReuseOperatorPermission()
    {
        var a = Authority(); await using var lease = await a.AcquireAsync(); var op = SyntheticApproval.Request();
        Assert.True(a.CanDecide(trust.Operator, op, "operator:readonly"));
        Assert.False(a.CanDecide(trust.Operator, op with { Scope = op.Scope with { ResourceId = "other" } }, "operator:readonly"));
        Assert.False(a.CanDecide(trust.Operator, op with { Epoch = new(2) }, "operator:readonly"));
        Assert.False(a.CanDecide(trust.Operator, op, "read:list"));
    }
    [Fact]
    public async Task RevokedOperatorCannotConsumePersistedApproval()
    {
        await Setup(); var s = Store(Authority()); var r = await Approved(s); trust.Rights = false;
        Assert.Equal("OperatorNoLongerAuthorized", (await Assert.ThrowsAsync<InvalidOperationException>(() => Consume(s, r))).Message);
        Assert.Equal(ApprovalStatus.Approved, (await s.GetAsync(r.ApprovalId))!.Status);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
    }
    [Fact]
    public async Task NewPolicyActivationWinsBeforeAdmission()
    {
        await Setup(); var s = Store(Authority()); var r = await Approved(s);
        source.Candidate = Wrap(Next()); await s.ActivatePolicyAsync();
        Assert.Equal("ApprovalPolicyChanged", (await Assert.ThrowsAsync<InvalidOperationException>(() => Consume(s, r))).Message);
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
    }
    [Fact]
    public async Task AdmissionLeaseSerializesConcurrentActivationThroughCommit()
    {
        await Setup(); var a = Authority(); var s = Store(a); var r = await Approved(s);
        using var release = new ManualResetEventSlim();
        var checkpoint = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guarded = Store(a, point =>
        {
            if (point != ApprovalCheckpoint.ExecutionInserted) return;
            checkpoint.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test checkpoint timeout");
        });
        var consume = Task.Run(() => Consume(guarded, r));
        try
        {
            await checkpoint.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var reads = source.Reads; source.Candidate = Wrap(Next());
            var activate = s.ActivatePolicyAsync(); // reaches the held async authority gate deterministically
            Assert.False(activate.IsCompleted); Assert.Equal(reads, source.Reads);
            release.Set(); await consume.WaitAsync(TimeSpan.FromSeconds(10)); await activate.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1L, await Sql("SELECT count(*) FROM executions"));
            Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
        }
        finally { release.Set(); await consume.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    [Fact]
    public async Task OtherAuthorityInstanceAdvancingRejectsStaleSource()
    {
        await Setup(); var old = Authority(); var fresh = Authority();
        await Store(old).ActivatePolicyAsync(); await using var lease = await old.AcquireAsync();
        source.Candidate = Wrap(Next()); await Store(fresh).ActivatePolicyAsync();
        // Independent stale instance must not roll the durable authority back after another activation.
        // Release its held lease before calling the store, which reacquires from the stale trusted source.
        await lease.DisposeAsync(); source.Candidate = Wrap(SyntheticApproval.Policy);
        Assert.Equal("AuthorityRollback", (await Assert.ThrowsAsync<InvalidOperationException>(() => Store(old).ActivatePolicyAsync())).Message);
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
    }
    [Fact]
    public async Task RestartUsesDurableHighWaterAndOriginalApprovalIdentity()
    {
        await Setup(); var s = Store(Authority()); var r = await Approved(s);
        Assert.Equal(r, await Store(Authority()).GetAsync(r.ApprovalId));
        source.Candidate = Wrap(Next()); await Store(Authority()).ActivatePolicyAsync();
        source.Candidate = Wrap(SyntheticApproval.Policy); // new instance has no in-memory high-water
        Assert.Equal("AuthorityRollback", (await Assert.ThrowsAsync<InvalidOperationException>(() => Store(Authority()).ActivatePolicyAsync())).Message);
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    [Fact]
    public async Task FailedBusinessMutationCannotRollbackActivation()
    {
        await Setup(); var s = Store(Authority()); await s.ActivatePolicyAsync(); source.Candidate = Wrap(Next());
        await Assert.ThrowsAsync<InvalidOperationException>(() => s.RequestAsync("new-approval", SyntheticApproval.Request(), trust.Caller));
        Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority")); Assert.Equal(0L, await Sql("SELECT count(*) FROM approval_requests"));
    }
    [Fact]
    public async Task AuditFullBlocksActivationWithoutDeletingEvidence()
    {
        await Setup(); var s = Store(Authority(), capacity: 1); await s.ActivatePolicyAsync(); var audit = await s.ReadAuditAsync();
        source.Candidate = Wrap(Next());
        Assert.Equal("AuditCapacityReached", (await Assert.ThrowsAsync<InvalidOperationException>(() => s.ActivatePolicyAsync())).Message);
        Assert.Equal(audit, await s.ReadAuditAsync()); Assert.Equal(1L, await Sql("SELECT epoch FROM policy_authority"));
    }
    [Fact]
    public async Task CancellationAndDisposalDoNotLeakAuthorityLease()
    {
        var a = Authority(); var lease = await a.AcquireAsync(); using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => a.AcquireAsync(cts.Token).AsTask());
        await lease.DisposeAsync(); await lease.DisposeAsync(); Assert.Null(a.GetSnapshot());
        await using var next = await a.AcquireAsync(); Assert.NotNull(a.GetSnapshot());
    }
    [Fact]
    public async Task ClockRollbackOrExpiredPolicyDoesNotUseCachedSnapshot()
    {
        var a = Authority(); await using (await a.AcquireAsync()) { }
        clock.Now = clock.Now.AddMinutes(-1);
        Assert.Equal("InvalidAuthorityPolicy", (await Assert.ThrowsAsync<InvalidOperationException>(() => a.AcquireAsync().AsTask())).Message);
        clock.Now = SyntheticApproval.Policy.NotAfter;
        Assert.Equal("InvalidAuthorityPolicy", (await Assert.ThrowsAsync<InvalidOperationException>(() => a.AcquireAsync().AsTask())).Message);
        Assert.Null(a.GetSnapshot());
    }
    [Theory]
    [InlineData("epoch")][InlineData("capability")][InlineData("lifetime")][InlineData("scope")]
    public async Task InvalidPolicyCannotBeActivated(string variant)
    {
        var p = SyntheticApproval.Policy;
        p = variant switch
        {
            "epoch" => p with { Epoch = new(0) },
            "capability" => p with { Capabilities = [SyntheticApproval.Descriptor with { Capability = (PolicyCapability)900 }] },
            "lifetime" => p with { DecisionLifetime = TimeSpan.Zero },
            _ => p with { Rules = [p.Rules[0] with { Scope = p.Rules[0].Scope with { Access = ResourceAccess.Unknown } }] }
        };
        source.Candidate = new("protected-test-source", p, new string('A', 64));
        Assert.Equal("InvalidAuthorityPolicy", (await Assert.ThrowsAsync<InvalidOperationException>(() => Authority().AcquireAsync().AsTask())).Message);
    }
    [Fact]
    public async Task MissingProductionResolverCannotDecideExistingPending()
    {
        await Setup(); var r = await Store(Authority()).RequestAsync("approval-one", SyntheticApproval.Request(), trust.Caller);
        var unavailable = Store(Authority(false)); await unavailable.ActivatePolicyAsync();
        Assert.Equal("OperatorNotAuthorized", (await Assert.ThrowsAsync<InvalidOperationException>(() => unavailable.DecideAsync(
            new(r.ApprovalId, "decision-one", r.Version, r.BindingDigest, ApprovalChoice.Approve), trust.Operator))).Message);
        Assert.Equal(ApprovalStatus.Pending, (await unavailable.GetAsync(r.ApprovalId))!.Status);
        Assert.Equal(0L, await Sql("SELECT count(*) FROM executions"));
    }
    [Fact]
    public async Task IndependentAuthorityInstancesSerializeAtSqliteActivationCommit()
    {
        await Setup(); var old = Authority(); var s = Store(old); var r = await Approved(s);
        var secondSource = new Source { Candidate = Wrap(Next()) };
        var second = new TrustedPolicyAuthority("protected-test-source", "authority", secondSource, clock, trust, trust, trust);
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var guarded = Store(old, point =>
        {
            if (point != ApprovalCheckpoint.ExecutionInserted) return;
            held.TrySetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("test checkpoint timeout");
        });
        var consume = Task.Run(() => Consume(guarded, r)); Task? activation = null;
        try
        {
            await held.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var loaded = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            secondSource.OnAcquire = () => loaded.TrySetResult();
            activation = Task.Run(() => Store(second).ActivatePolicyAsync());
            await loaded.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(activation.IsCompleted); // first transaction still owns the SQLite writer
            release.Set(); await consume.WaitAsync(TimeSpan.FromSeconds(10));
            await activation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1L, await Sql("SELECT count(*) FROM executions")); Assert.Equal(2L, await Sql("SELECT epoch FROM policy_authority"));
        }
        finally { release.Set(); await consume.WaitAsync(TimeSpan.FromSeconds(10)); }
    }
    sealed class MaintenanceAuthorization(string expected, bool allowed = true, bool unavailable = false) : ITrustedApprovalMaintenanceAuthorization
    {
        public bool CanMigrate(string resource) => unavailable ? throw new IOException("private detail") : allowed && resource == expected;
    }
    [Theory]
    [InlineData("missing", "MaintenanceNotAuthorized")]
    [InlineData("wrong-resource", "MaintenanceNotAuthorized")]
    [InlineData("denied", "MaintenanceNotAuthorized")]
    [InlineData("unavailable", "MaintenanceAuthorizationUnavailable")]
    public async Task MaintenanceRequiresTrustedExactResourceAuthorization(string variant, string reason)
    {
        ITrustedApprovalMaintenanceAuthorization? auth = variant == "missing" ? null : new MaintenanceAuthorization(
            variant == "wrong-resource" ? Db + ".other" : Db, variant != "denied", variant == "unavailable");
        Assert.Equal(reason, (await Assert.ThrowsAsync<InvalidOperationException>(() =>
            AuthorizedApprovalMaintenance.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance, auth))).Message);
        Assert.False(File.Exists(Db));
    }
    [Fact]
    public async Task AuthorizedMaintenanceStillRequiresStopAndBackupAndRejectsV2Remigration()
    {
        var auth = new MaintenanceAuthorization(Db);
        await Assert.ThrowsAsync<InvalidOperationException>(() => AuthorizedApprovalMaintenance.InitializeOrMigrateAsync(Db, new(true, false, true), auth));
        Assert.False(File.Exists(Db));
        await AuthorizedApprovalMaintenance.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance, auth);
        Assert.Equal(2L, await Sql("PRAGMA user_version"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AuthorizedApprovalMaintenance.InitializeOrMigrateAsync(Db, SyntheticApproval.Maintenance, auth));
        Assert.Equal("ok", await Sql("PRAGMA integrity_check"));
    }
    public void Dispose() { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
}
