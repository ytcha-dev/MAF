using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;
using MAF.Workflows.External;

namespace MAF.ApprovalAcceptance;

// Synthetic human claims exist ONLY in this test executable, never production trust.
public sealed class SyntheticApprovalApplication : ITrustedIdentityResolver, ITrustedPolicyAcquisition, IApprovalAuthorization, IExecutionBindingVerifier
{
    public SyntheticRuntime Runtime { get; }
    public SyntheticActor Caller => Runtime.Caller;
    public SyntheticActor Operator { get; } = new("synthetic-operator");
    public SyntheticActor Self { get; } = new("runtime-agent", "synthetic-human");
    public SyntheticClock Clock => Runtime.Clock;
    public PolicySnapshot Policy { get; set; }
    public AuthenticatedIdentity OperatorIdentity { get; set; }
    public bool OperatorEnabled { get; set; } = true;
    public SyntheticApprovalApplication(string database, TimeSpan? operationTimeout = null)
    {
        Runtime = new(database, operationTimeout);
        Policy = Runtime.Policy with { Rules = [Runtime.Policy.Rules[0] with { Outcome = PolicyOutcome.RequireApproval, RequiredOperatorScope = "operator:readonly" }, Runtime.Policy.Rules[1]] };
        OperatorIdentity = new(Operator.SubjectId, Operator.AuthenticationContextId, IdentityKind.HumanOperator, Clock.Now.AddMinutes(-1), Clock.Now.AddMinutes(10), true);
    }
    public AuthenticatedIdentity? Resolve(ITrustedCallerContext context)
        => ReferenceEquals(context, Operator) ? OperatorIdentity
        : ReferenceEquals(context, Self) ? OperatorIdentity with { SubjectId = Self.SubjectId, AuthenticationContextId = Self.AuthenticationContextId }
        : Runtime.Resolve(context);
    public Task<AcquiredPolicy?> AcquireAsync(CancellationToken token)
        => Task.FromResult<AcquiredPolicy?>(new("runtime-source", Policy, PolicySnapshotBinding.Digest(Policy)));
    public bool CanDecide(AuthenticatedIdentity identity, PolicyRequest op, string digest, string scope, PolicySnapshot policy)
        => OperatorEnabled && identity.SubjectId == Operator.SubjectId && scope == "operator:readonly" && op.Scope == Runtime.Scope
            && digest == OperationBinding.Digest(op, Runtime.Submit);
    public bool CanControl(AuthenticatedIdentity identity, PolicyRequest op, PolicySnapshot policy) => Runtime.CanControl(identity, op, policy);
    public bool IsDecisionSubjectStillAuthorized(string subject, PolicyRequest op, string digest, string scope, PolicySnapshot policy)
        => OperatorEnabled && subject == Operator.SubjectId && scope == "operator:readonly";
    public bool Matches(PolicyRequest op, DurableExecution execution) => Runtime.Matches(op, execution);
    public TrustedPolicyAuthority Authority() => new("runtime-source", "runtime-authority", this, Clock, this, this, this);
    public SqliteApprovalStore Store() => new(Runtime.Database, Authority(), Clock);
    public ProtectedExternalRuntime ProtectedRuntime(HttpClient http, SqliteApprovalStore? coordinator = null)
    {
        var sql = coordinator ?? Store();
        return new(Runtime.Inputs, new ImmutablePolicySource(Policy), this,
            new SqliteExecutionStore(Runtime.Database, ExecutionStoreProfile.ExplicitV2), sql, sql, sql, http, Runtime.Options, Clock);
    }
    public DurableApprovalApplication Application(IApprovalTransactionCoordinator? coordinator = null, IExecutionStore? executions = null)
        => new(Runtime.Inputs, new ImmutablePolicySource(Policy), this,
            executions ?? new SqliteExecutionStore(Runtime.Database, ExecutionStoreProfile.ExplicitV2), coordinator ?? Store(), Clock);
}
