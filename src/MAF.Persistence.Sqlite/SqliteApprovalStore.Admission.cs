using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore
{
    // Fixed direct-Allow operation. Approval consumption remains a separate lifecycle.
    public async Task<AdmissionContinuation> AdmitAllowedAsync(PolicyRequest operation, PolicyDecision assessment,
        ExternalTaskRequest request, DurableExecution initialExecution, long expectedControlVersion,
        ITrustedCallerContext caller, IExecutionOwnership ownership, CancellationToken token = default)
    {
        await using var ownershipUse = await SqliteExecutionStore.UseAdmissionOwnershipAsync(path, operation.TaskId, ownership, token, ExecutionProfile);
        await using var authorityLease = await authority.AcquireAsync(token);
        Metadata(operation); var actor = Actor(caller); var policy = Candidate();
        void Validate()
        {
            var decision = new DeterministicPolicyEvaluator(new ImmutablePolicySource(policy), clock).Revalidate(operation, caller, assessment);
            Require(decision.Outcome == PolicyOutcome.Allow && decision.Reason == PolicyReason.Allowed, "AllowAdmissionRequired");
            Require(authority.CanControl(caller, operation), "CallerNotAuthorized");
            Require(operation.Operation == PolicyOperation.SubmitNewReadOnlyTask && operation.IntentGeneration == 1
                && operation.ProviderSessionId is null, "DurableNewSubmissionRequired");
            Require(request.TaskId == operation.TaskId && request.Policy == ExternalExecutionPolicy.ReadOnly
                && request.TaskText is { Length: > 0 and <= 4000 }
                && request.Timeout >= TimeSpan.FromMilliseconds(10) && request.Timeout <= TimeSpan.FromMinutes(10)
                && request.Timeout.Ticks == operation.Scope.BudgetTicks
                && DurableExecution.NormalizeRoot(request.ProjectRoot) == operation.Scope.ResourceId
                && DurableExecution.Fingerprint(request) == operation.RequestFingerprint, "RequestBindingMismatch");
            var e = initialExecution; var op = operation;
            Require(e.TaskId == op.TaskId && e.WorkflowRunId == op.WorkflowRunId && e.Handle.ExecutionId == op.ExecutionId
                && e.Handle.ExternalRequestId == op.MessageId && e.Handle.ExternalSessionId is null && e.RequestFingerprint == op.RequestFingerprint
                && e.ProviderScope == op.Scope.ProviderScopeDigest && e.ProjectRoot == op.Scope.ResourceId && e.State == DurableExecutionState.Created
                && e.SubmissionPhase == SubmissionPhase.None && e.Version == 0 && e.CompletedAt is null && e.ReconciledAt is null
                && e.ErrorCode is null && !e.AbortRequested && e.CreatedAt == e.UpdatedAt && e.CreatedAt >= assessment.EvaluatedAt
                && e.CreatedAt <= clock.GetUtcNow(), "ExecutionBindingMismatch");
            Id(e.Handle.Provider);
            Require(authority.MatchesExecutionBinding(op, e), "ExecutionProviderBindingMismatch");
        }
        await GuardedTransaction(policy, async (c, tx) =>
        {
            Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx, "SELECT count(*) FROM executions WHERE task_id=$t", token,
                ("$t", operation.TaskId))) == 0, "ExistingExecutionRecoveryOnly");
            Validate();
            var control = await Control(c, tx, operation.TaskId, token);
            Require(expectedControlVersion >= 0 && (control is null ? expectedControlVersion == 0
                : control.ExecutionId == operation.ExecutionId && control.Version == expectedControlVersion && control.CancelRequestId is null), "ControlConflict");
            Require(Convert.ToInt64(await ApprovalDatabase.Scalar(c, tx,
                "SELECT count(*) FROM authorization_operations WHERE task_id=$t AND kind=1", token, ("$t", operation.TaskId))) == 0,
                "InitialOperationAlreadyExists");
            if (control is null)
                await ApprovalDatabase.Execute(c, tx, "INSERT INTO task_controls(task_id,execution_id,version) VALUES($t,$e,0)", token,
                    ("$t", operation.TaskId), ("$e", operation.ExecutionId));
            await ApprovalDatabase.Execute(c, tx, "INSERT INTO authorization_operations VALUES($o,$t,$e,$k,$g)", token,
                ("$o", operation.OperationId), ("$t", operation.TaskId), ("$e", operation.ExecutionId), ("$k", (int)operation.Operation), ("$g", operation.IntentGeneration));
            await ApprovalDatabase.Execute(c, tx, profile == ApprovalStoreProfile.ExplicitV4 ? "INSERT INTO executions(task_id,version,terminal,snapshot,session_receipt_required) VALUES($t,0,0,$j,1)" : "INSERT INTO executions VALUES($t,0,0,$j)", token,
                ("$t", initialExecution.TaskId), ("$j", Json(initialExecution)));
            fault?.Invoke(ApprovalCheckpoint.ExecutionInserted);
            // Existing v2 Requested code, null approval_id: direct admission, never an Approved row.
            await Audit(c, tx, ControlAuditCode.Requested, actor, operation.TaskId, null, 0, token);
            Validate();
            return true;
        }, token);
        fault?.Invoke(ApprovalCheckpoint.AdmissionCommitted);
        return new AdmissionContinuation(initialExecution, ownership,
            (owner, ct) => SqliteExecutionStore.UseAdmissionOwnershipAsync(path, operation.TaskId, owner, ct, ExecutionProfile));
    }
}
