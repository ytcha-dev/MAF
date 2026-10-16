using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Workflows.External;

// Isolated, unregistered application seam. No provider or Workflow reference/callback is accepted.
public sealed class AtomicAdmissionApplication(ExternalAdmissionInputs inputs, IPolicySource? policy,
    ITrustedIdentityResolver? identities, IExecutionAdmissionCoordinator coordinator, TimeProvider clock)
{
    readonly ExternalExecutionApplication assessor = new(inputs, policy, identities, clock);

    public Task<AdmissionContinuation> AdmitAsync(ExternalAdmissionRequest request, ITrustedCallerContext caller,
        IExecutionOwnership ownership, long expectedControlVersion = 0, CancellationToken token = default)
    {
        var assessment = assessor.Assess(request, caller);
        if (assessment.Classification == ExternalAdmissionClassification.ExistingInspection)
            throw new InvalidOperationException("ExistingExecutionRecoveryOnly");
        if (assessment.Classification != ExternalAdmissionClassification.NewSubmission)
            throw new InvalidOperationException("DurableNewSubmissionRequired");
        if (assessment.Decision.Outcome != PolicyOutcome.Allow || assessment.Operation is not { } op)
            throw new InvalidOperationException("AllowAdmissionRequired");
        var now = clock.GetUtcNow();
        var initial = new DurableExecution(op.TaskId, op.RequestFingerprint, op.Scope.ProviderScopeDigest,
            op.Scope.ResourceId, op.WorkflowRunId,
            new(op.ExecutionId, inputs.ProviderId, ExternalRequestId: op.MessageId),
            DurableExecutionState.Created, SubmissionPhase.None, now, now);
        return coordinator.AdmitAllowedAsync(op, assessment.Decision, request.Submission!, initial,
            expectedControlVersion, caller, ownership, token);
    }
}
