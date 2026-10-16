using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Persistence.Sqlite;

public sealed partial class SqliteApprovalStore : IRuntimeEnforcementCoordinator
{
    public Task VerifyEffectAsync(PolicyRequest operation, PolicyDecision assessment, DurableExecution expected,
        DurableExecution? intent, long controlVersion, ProviderEffect effect, ITrustedCallerContext caller,
        IExecutionOwnership ownership, CancellationToken token = default)
        => VerifyEffectTransactionAsync(operation, assessment, expected, intent, controlVersion, effect, caller, ownership, token);

    public Task VerifyApprovedEffectAsync(ConsumedApprovalAuthorization evidence, PolicyRequest operation, PolicyDecision assessment,
        DurableExecution expected, DurableExecution? intent, long controlVersion, ProviderEffect effect,
        ITrustedCallerContext caller, IExecutionOwnership owner, CancellationToken token = default)
        => VerifyEffectTransactionAsync(operation, assessment, expected, intent, controlVersion, effect, caller, owner, token,
            evidence ?? throw new InvalidOperationException("ConsumedApprovalRequired"));
    async Task VerifyEffectTransactionAsync(PolicyRequest operation, PolicyDecision assessment, DurableExecution expected,
        DurableExecution? intent, long controlVersion, ProviderEffect effect, ITrustedCallerContext caller,
        IExecutionOwnership ownership, CancellationToken token, ConsumedApprovalAuthorization? approval = null)
    {
        if (approval is not null) Require(approval.Database == path && ReferenceEquals(approval.Owner, ownership), "ResumeOwnershipMismatch");
        await using var use = await SqliteExecutionStore.UseAdmissionOwnershipAsync(path, operation.TaskId, ownership, token);
        await using var lease = await authority.AcquireAsync(token);
        var policy = Candidate(); Metadata(operation); var actor = Actor(caller);
        await GuardedTransaction(policy, async (c, tx) =>
        {
            void Authorized()
            {
                Require(Enum.IsDefined(effect) && effect != ProviderEffect.Abort, "EffectNotSupported");
                var required = effect is ProviderEffect.Observe or ProviderEffect.Reattach or ProviderEffect.Metadata
                    ? PolicyOperation.InspectExistingExecution : PolicyOperation.SubmitNewReadOnlyTask;
                if (effect == ProviderEffect.Validate && operation.Operation == PolicyOperation.InspectExistingExecution)
                    required = PolicyOperation.InspectExistingExecution;
                Require(operation.Operation == required, "EffectOperationMismatch");
                var evaluator = new DeterministicPolicyEvaluator(new ImmutablePolicySource(policy), clock);
                var approvedSubmit = approval is not null && required == PolicyOperation.SubmitNewReadOnlyTask;
                var d = approvedSubmit ? evaluator.Evaluate(operation, caller) : evaluator.Revalidate(operation, caller, assessment);
                Require(d.Outcome == (approvedSubmit ? PolicyOutcome.RequireApproval : PolicyOutcome.Allow)
                    && assessment.Outcome == d.Outcome && assessment.Reason == d.Reason
                    && assessment.AuthorityId == d.AuthorityId && assessment.Epoch == d.Epoch && assessment.Version == d.Version
                    && assessment.RequiredOperatorScope == d.RequiredOperatorScope
                    && d.BindingDigest == assessment.BindingDigest && clock.GetUtcNow() >= assessment.EvaluatedAt && clock.GetUtcNow() < assessment.NotAfter
                    && authority.CanControl(caller, operation), "EffectNotAuthorized");
                var e = expected;
                Require(operation.TaskId == e.TaskId && operation.WorkflowRunId == e.WorkflowRunId
                    && operation.ExecutionId == e.Handle.ExecutionId && operation.MessageId == e.Handle.ExternalRequestId
                    && operation.ProviderSessionId == e.Handle.ExternalSessionId && operation.RequestFingerprint == e.RequestFingerprint
                    && operation.Scope.ResourceId == e.ProjectRoot && operation.Scope.ProviderScopeDigest == e.ProviderScope
                    && authority.MatchesExecutionBinding(operation, e), "EffectBindingMismatch");
            }
            if (approval is not null) await VerifyConsumed(c, tx, approval, policy, operation, token);
            Authorized();
            var control = await Control(c, tx, expected.TaskId, token);
            Require(control is not null && control.ExecutionId == expected.Handle.ExecutionId && control.Version == controlVersion
                && (operation.Operation == PolicyOperation.InspectExistingExecution || control.CancelRequestId is null), "ControlConflict");
            Require((string?)await ApprovalDatabase.Scalar(c, tx, "SELECT snapshot FROM executions WHERE task_id=$t", token,
                ("$t", expected.TaskId)) == Json(expected), "ExecutionVersionConflict");
            if (intent is not null)
            {
                Require(effect == ProviderEffect.CreateSession && expected.State == DurableExecutionState.Created
                    && intent.State == DurableExecutionState.SessionCreating && intent.SubmissionPhase == SubmissionPhase.SessionCreationIntended
                    || effect == ProviderEffect.SubmitPrompt && expected.State == DurableExecutionState.ReadyToSubmit
                    && intent.State == DurableExecutionState.SubmissionIntended && intent.SubmissionPhase == SubmissionPhase.PromptIntended,
                    "InvalidDispatchIntent");
                DurableExecutionTransitions.Validate(expected, intent);
                Require(await ApprovalDatabase.Execute(c, tx,
                    "UPDATE executions SET version=$v,terminal=0,snapshot=$j WHERE task_id=$t AND version=$old AND snapshot=$before", token,
                    ("$v", intent.Version), ("$j", Json(intent)), ("$t", expected.TaskId), ("$old", expected.Version), ("$before", Json(expected))) == 1,
                    "ExecutionVersionConflict");
                await Audit(c, tx, ControlAuditCode.Requested, actor, expected.TaskId, approval?.Approval.ApprovalId, intent.Version, token);
                fault?.Invoke(ApprovalCheckpoint.DispatchIntentWritten);
            }
            else if (effect == ProviderEffect.CreateSession)
                Require(expected.State == DurableExecutionState.SessionCreating && expected.SubmissionPhase == SubmissionPhase.SessionCreationIntended
                    && expected.Handle.ExternalSessionId is null, "DispatchIntentRequired");
            else if (effect == ProviderEffect.SubmitPrompt)
                Require(expected.State == DurableExecutionState.SubmissionIntended && expected.SubmissionPhase == SubmissionPhase.PromptIntended
                    && expected.Handle.ExternalSessionId is not null, "DispatchIntentRequired");
            if (approval is not null) await VerifyConsumed(c, tx, approval, policy, operation, token);
            Authorized();
            return true;
        }, token);
        if (intent is not null) fault?.Invoke(ApprovalCheckpoint.DispatchIntentCommitted);
    }
}
