using MAF.Core;
using MAF.Core.Policy;
using MAF.Persistence.Sqlite;

namespace MAF.ApprovalAcceptance;

// Test-only fixtures: never referenced by Host or production assemblies.
public sealed record SyntheticActor(string SubjectId, string AuthenticationContextId = "synthetic-context") : ITrustedCallerContext;
public sealed class SyntheticClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
public sealed class SyntheticAuthority : IProtectedPolicyAuthority
{
    public PolicySnapshot Snapshot { get; set; } = SyntheticApproval.Policy;
    public bool OperatorEnabled { get; set; } = true;
    public PolicySnapshot GetSnapshot() => Snapshot;
    public PolicyDecision EvaluateStoredOperation(PolicyRequest operation, DateTimeOffset at)
        => new DeterministicPolicyEvaluator(new ImmutablePolicySource(Snapshot), new FixedClock(at)).Evaluate(operation, new SyntheticActor(operation.CallerSubject));
    public bool CanDecide(ITrustedCallerContext actor, PolicyRequest operation, string requiredOperatorScope)
        => OperatorEnabled && actor.SubjectId == "operator" && actor.AuthenticationContextId == "synthetic-context" && requiredOperatorScope == "operator:readonly";
    public bool IsDecisionSubjectStillAuthorized(string recordedSubject, PolicyRequest operation, string requiredOperatorScope)
        => OperatorEnabled && recordedSubject == "operator" && requiredOperatorScope == "operator:readonly";
    public bool CanControl(ITrustedCallerContext actor, PolicyRequest operation)
        => actor.SubjectId == operation.CallerSubject && actor.AuthenticationContextId == "synthetic-context";
    public bool MatchesExecutionBinding(PolicyRequest operation, DurableExecution execution)
        => execution.Handle.Provider == "Synthetic" && operation.ConnectorId == SyntheticApproval.Descriptor.ConnectorId;
    sealed class FixedClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
public static class SyntheticApproval
{
    public static readonly SyntheticActor Caller = new("submitter"), Operator = new("operator");
    public static readonly AuthorizationScope Scope = new("synthetic-resource", new('A', 64), new('B', 64), 1, 1, ResourceAccess.ReadOnly, 300000000);
    public static readonly CapabilityDescriptor Descriptor = new("synthetic-connector", "1", PolicyCapability.ReadOnlyTask,
        EnforcementBoundary.TaskAdmission, [PolicyOperation.SubmitNewReadOnlyTask], true, true);
    public static PolicyRequest Request(string suffix = "one") => new("authority", "submitter", "operation-" + suffix, "task-" + suffix,
        "run-" + suffix, "execution-" + suffix, "message-" + suffix, new('C', 64), Descriptor.ConnectorId, Descriptor.Capability,
        PolicyOperation.SubmitNewReadOnlyTask, Scope, new(1), new(1));
    public static PolicySnapshot Policy => new("authority", new(1), new(1), new SyntheticClock().Now.AddHours(-1), new SyntheticClock().Now.AddHours(1),
        TimeSpan.FromMinutes(5), [Descriptor], [new("submitter", Descriptor.ConnectorId, Descriptor.Capability, PolicyOperation.SubmitNewReadOnlyTask,
            Scope, PolicyOutcome.RequireApproval, "operator:readonly")]);
    public static readonly OfflineMaintenance Maintenance = new(true, true, true);
    public static DurableExecution Execution(PolicyRequest r, DateTimeOffset at) => new(r.TaskId, r.RequestFingerprint, r.Scope.ProviderScopeDigest,
        r.Scope.ResourceId, r.WorkflowRunId, new(r.ExecutionId, "Synthetic", ExternalRequestId: r.MessageId), DurableExecutionState.Created,
        SubmissionPhase.None, at, at);
}
