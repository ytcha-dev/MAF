using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;

namespace MAF.ApprovalAcceptance;

// Test-owned identities/policy only. Never registered by Host or production composition.
public sealed class SyntheticCancellation : ITrustedIdentityResolver, ITrustedPolicyAcquisition, IApprovalAuthorization,
    IExecutionBindingVerifier, ITrustedApprovalMaintenanceAuthorization
{
    public SyntheticApprovalApplication Approval { get; }
    public bool V4 { get; }
    public SyntheticRuntime Runtime => Approval.Runtime;
    public SyntheticClock Clock => Runtime.Clock;
    public SyntheticActor Canceller { get; set; } = new("synthetic-canceller");
    public bool Available = true, Enabled = true;
    public bool ExpiredIdentity;
    public Action? Acquired;
    public PolicySnapshot Policy { get => Approval.Policy; set => Approval.Policy = value; }
    public CapabilityDescriptor Capability => Runtime.Submit with { Capability = PolicyCapability.ExecutionCancellation, SupportedOperations = [PolicyOperation.RequestCancelExecution] };
    public CancellationAdmissionInputs Inputs => new(Policy.AuthorityId, Policy.Epoch, Policy.Version, Runtime.Scope, Capability, "cancel-operation");
    public CancellationCommand Command => new("cancel-one", Runtime.Request.TaskId, "runtime-execution", 0);
    public SyntheticCancellation(string database, bool approved = false, bool v4 = false, TimeSpan? operationTimeout = null)
    {
        V4 = v4;
        Directory.CreateDirectory(Path.GetDirectoryName(database)!);
        Approval = new(database, operationTimeout);
        var original = approved ? Approval.Policy : Runtime.Policy;
        Policy = original with { Capabilities = [..original.Capabilities, Capability], Rules = [..original.Rules,
            new(Canceller.SubjectId, Capability.ConnectorId, Capability.Capability, PolicyOperation.RequestCancelExecution, Runtime.Scope, PolicyOutcome.Allow)] };
    }
    public bool CanMigrate(string path) => path == Path.GetFullPath(Runtime.Database);
    public AuthenticatedIdentity? Resolve(ITrustedCallerContext caller)
    {
        var identity = ReferenceEquals(caller, Canceller)
        ? new(Canceller.SubjectId, Canceller.AuthenticationContextId, IdentityKind.Agent, Clock.Now.AddHours(-1), Clock.Now.AddHours(1), false) : Approval.Resolve(caller);
        return ExpiredIdentity && identity is not null ? identity with { NotAfter = Clock.Now.AddTicks(-1) } : identity;
    }
    public Task<AcquiredPolicy?> AcquireAsync(CancellationToken token)
    { Acquired?.Invoke(); return Task.FromResult<AcquiredPolicy?>(Available ? new("runtime-source", Policy, PolicySnapshotBinding.Digest(Policy)) : null); }
    public bool CanControl(AuthenticatedIdentity identity, PolicyRequest op, PolicySnapshot policy)
        => Enabled && (identity.SubjectId == Canceller.SubjectId ? op.Operation == PolicyOperation.RequestCancelExecution : Approval.CanControl(identity, op, policy));
    public bool CanDecide(AuthenticatedIdentity identity, PolicyRequest op, string digest, string scope, PolicySnapshot policy) => Approval.CanDecide(identity, op, digest, scope, policy);
    public bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest op, string digest, string scope, PolicySnapshot policy) => Approval.IsDecisionSubjectStillAuthorized(subject, op, digest, scope, policy);
    public bool Matches(PolicyRequest op, DurableExecution execution) => Runtime.Matches(op, execution);
    public TrustedPolicyAuthority Authority() => new("runtime-source", "runtime-authority", this, Clock, this, this, this);
    public SqliteApprovalStore Store(Action<string>? checkpoint = null, int capacity = 10000)
        => new(Runtime.Database, Authority(), Clock, capacity, null, V4 ? ApprovalStoreProfile.ExplicitV4 : ApprovalStoreProfile.ExplicitV3, checkpoint);
    public SqliteExecutionStore Executions => new(Runtime.Database, V4 ? ExecutionStoreProfile.ExplicitV4 : ExecutionStoreProfile.ExplicitV3);
    public CancellationAdmissionApplication Application(SqliteApprovalStore? store = null, CancellationAdmissionInputs? inputs = null)
        => new(inputs ?? Inputs, this, store ?? Store(), Clock);
    public ProtectedExternalRuntime Protected(HttpClient http, SqliteApprovalStore? store = null)
    {
        var sql = store ?? Store(); return new(Runtime.Inputs with { Epoch = Policy.Epoch, Version = Policy.Version }, new ImmutablePolicySource(Policy), this,
            Executions, sql, sql, sql, http, Runtime.Options, Clock);
    }
    public async Task Setup(bool pending = false)
    {
        await ApprovalSchema.InitializeOrMigrateAsync(Runtime.Database, SyntheticApproval.Maintenance);
        await ApprovalSchema.MigrateV2ToV3Async(Runtime.Database, SyntheticApproval.Maintenance, this);
        if (V4) await ApprovalSchema.MigrateV3ToV4Async(Runtime.Database, SyntheticApproval.Maintenance, this);
        await Store().ActivatePolicyAsync();
        if (pending)
            await new DurableApprovalApplication(Runtime.Inputs, new ImmutablePolicySource(Policy), this, Executions, Store(), Clock)
                .RequestAsync("cancel-approval", Runtime.Intent, Runtime.Caller);
        else
        {
            await using var owner = (await Executions.TryAcquireAsync(Runtime.Request.TaskId))!;
            var continuation = await new AtomicAdmissionApplication(Runtime.Inputs, new ImmutablePolicySource(Policy), this, Store(), Clock)
                .AdmitAsync(Runtime.Intent, Runtime.Caller, owner);
            _ = await continuation.ConsumeAsync(owner);
        }
    }
}
