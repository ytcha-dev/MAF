namespace MAF.Core.Policy;

public enum ApprovalStatus { Pending, Approved, Rejected, Expired, Revoked, Consumed }
public enum ApprovalChoice { Approve, Reject }
public enum ControlAuditCode { Requested, Approved, Rejected, Expired, Revoked, Consumed, CancellationRequested, AuthorityAdvanced }

// A trusted deployment boundary, not a caller-provided epoch or authentication fallback.
// Implementations must protect policy configuration and serialize revision activation with storage maintenance.
// CanDecide/CanControl must validate the supplied trusted identity against the exact operation scope.
// M2-B supplies an unregistered adapter; production trust transports remain unavailable.
public interface IProtectedPolicyAuthority : IPolicySource
{
    // Lease must freeze the acquired policy until the storage commit completes.
    ValueTask<IAsyncDisposable> AcquireAsync(CancellationToken token = default);
    PolicyDecision EvaluateStoredOperation(PolicyRequest operation, DateTimeOffset evaluatedAt);
    bool CanDecide(ITrustedCallerContext actor, PolicyRequest operation, string requiredOperatorScope);
    bool IsDecisionSubjectStillAuthorized(string recordedSubject, PolicyRequest operation, string requiredOperatorScope);
    bool MatchesExecutionBinding(PolicyRequest operation, DurableExecution execution);
    bool CanControl(ITrustedCallerContext actor, PolicyRequest operation);
}

public sealed record ApprovalRecord(string ApprovalId, PolicyRequest Operation, string BindingDigest,
    string RequiredOperatorScope, ApprovalStatus Status, long Version, DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt, string? DecisionId = null, ApprovalChoice? Choice = null, string? DecidedBy = null);
public sealed record ApprovalDecisionCommand(string ApprovalId, string DecisionId, long ExpectedVersion,
    string ExpectedBindingDigest, ApprovalChoice Choice);
public sealed record TaskControl(string TaskId, string ExecutionId, long Version, string? CancelRequestId = null,
    string? RequestedBy = null, DateTimeOffset? RequestedAt = null);
public sealed record ControlAudit(long Sequence, ControlAuditCode Code, string Actor, string TaskId,
    string? ApprovalId, long Version, DateTimeOffset RecordedAt, PolicyEpoch Epoch, PolicyVersion PolicyVersion);

public interface IApprovalStore
{
    Task<ApprovalRecord?> GetAsync(string approvalId, CancellationToken token = default);
    Task<IReadOnlyList<ApprovalRecord>> ListPendingAsync(int limit = 100, CancellationToken token = default);
    Task<TaskControl?> GetControlAsync(string taskId, CancellationToken token = default);
    Task<IReadOnlyList<ControlAudit>> ReadAuditAsync(long afterSequence = 0, int limit = 100, CancellationToken token = default);
}

// Data transactions only. Consumption inserts a Created snapshot; it returns no dispatch ticket
// and cannot start a Workflow, submit a prompt, send an abort, or authenticate a human.
public interface IApprovalTransactionCoordinator
{
    Task<ApprovalRecord> RequestAsync(string approvalId, PolicyRequest operation, ITrustedCallerContext caller, CancellationToken token = default);
    Task<ApprovalRecord> DecideAsync(ApprovalDecisionCommand command, ITrustedCallerContext actor, CancellationToken token = default);
    Task<ApprovalRecord> ExpireAsync(string approvalId, long expectedVersion, CancellationToken token = default);
    Task<ApprovalRecord> RevokeAsync(string approvalId, long expectedVersion, ITrustedCallerContext actor, CancellationToken token = default);
    Task<DurableExecution> ConsumeAsync(string approvalId, long expectedApprovalVersion, long expectedControlVersion,
        string expectedBindingDigest, DurableExecution initialExecution, ITrustedCallerContext caller, CancellationToken token = default);
    Task<TaskControl> RequestCancellationAsync(string approvalId, string cancelRequestId, long expectedControlVersion,
        ITrustedCallerContext actor, CancellationToken token = default);
}

// No snapshot/epoch parameters: only a trusted source can propose the next revision.
// Activation becomes effective at the coordinator's durable high-water commit.
public interface IPolicyActivationCoordinator
{
    Task ActivatePolicyAsync(CancellationToken token = default);
}
