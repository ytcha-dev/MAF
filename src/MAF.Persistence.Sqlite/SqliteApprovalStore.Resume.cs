using MAF.Core;
using MAF.Core.Policy;
using Microsoft.Data.Sqlite;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore : IApprovalResumeCoordinator
{
    public async Task<AdmissionContinuation> ConsumeOwnedAsync(ApprovalResumeRequest request, PolicyRequest operation, DurableExecution initial,
        long controlVersion, ITrustedCallerContext caller, ITrustedCallerContext operatorContext, IExecutionOwnership owner, CancellationToken token = default)
    {
        Require(request.Input is not null, "InputRequired");
        await using var use = await SqliteExecutionStore.UseAdmissionOwnershipAsync(path, operation.TaskId, owner, token, ExecutionProfile);
        var consumed = await ConsumeTransactionAsync(request.ApprovalId, request.ApprovalVersion, controlVersion, request.BindingDigest,
            initial, caller, token, r =>
            {
                Current(r, Candidate()); // Recheck approval expiry at both ends of the short consumption transaction.
                Require(r.Operation == operation && operation.ProviderSessionId is null && operation.IntentGeneration == 1
                    && operation.Operation == PolicyOperation.SubmitNewReadOnlyTask, "ResumeOperationMismatch");
                Require(request.Input!.TaskId == operation.TaskId && request.Input.Timeout.Ticks == operation.Scope.BudgetTicks
                    && request.Input.Policy == ExternalExecutionPolicy.ReadOnly && request.Input.TaskText is { Length: > 0 and <= 4000 }
                    && DurableExecution.Fingerprint(request.Input) == operation.RequestFingerprint, "ResumeInputMismatch");
                VerifyResumeOperator(r, operatorContext);
            });
        fault?.Invoke(ApprovalCheckpoint.ConsumptionCommitted);
        var evidence = new ConsumedApprovalAuthorization(consumed.Approval, path, owner, operatorContext);
        return new AdmissionContinuation(consumed.Execution, owner,
            (o, ct) => SqliteExecutionStore.UseAdmissionOwnershipAsync(path, operation.TaskId, o, ct, ExecutionProfile), evidence);
    }

    void VerifyResumeOperator(ApprovalRecord r, ITrustedCallerContext operatorContext)
    {
        Require(r.DecisionId is not null && r.Choice == ApprovalChoice.Approve && r.DecidedBy is not null
            && operatorContext is not null && operatorContext.SubjectId == r.DecidedBy && r.DecidedBy != r.Operation.CallerSubject
            && authority.CanDecide(operatorContext, r.Operation, r.RequiredOperatorScope)
            && authority.IsDecisionSubjectStillAuthorized(r.DecidedBy, r.Operation, r.RequiredOperatorScope), "ResumeOperatorNotAuthorized");
    }

    async Task VerifyConsumed(SqliteConnection c, SqliteTransaction tx, ConsumedApprovalAuthorization evidence,
        PolicySnapshot policy, PolicyRequest operation, CancellationToken token)
    {
        var r = await Read(c, tx, evidence.Approval.ApprovalId, token) ?? throw new InvalidOperationException("UnknownApproval");
        Require(r == evidence.Approval && r.Status == ApprovalStatus.Consumed, "ConsumedApprovalMismatch");
        Current(r, policy); VerifyResumeOperator(r, evidence.Operator);
        Require(operation.TaskId == r.Operation.TaskId && operation.WorkflowRunId == r.Operation.WorkflowRunId
            && operation.ExecutionId == r.Operation.ExecutionId && operation.MessageId == r.Operation.MessageId
            && operation.CallerSubject == r.Operation.CallerSubject && operation.RequestFingerprint == r.Operation.RequestFingerprint
            && operation.Scope == r.Operation.Scope && operation.AuthorityId == r.Operation.AuthorityId
            && operation.Epoch == r.Operation.Epoch && operation.Version == r.Operation.Version, "ResumeEffectBindingMismatch");
        if (operation.Operation == PolicyOperation.SubmitNewReadOnlyTask)
            Require(operation with { ProviderSessionId = r.Operation.ProviderSessionId } == r.Operation, "ResumeEffectBindingMismatch");
    }
}
