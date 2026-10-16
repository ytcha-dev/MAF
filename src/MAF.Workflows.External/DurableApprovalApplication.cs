using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Workflows.External;

// Explicit, non-executable application entrypoint. Not installed in Host.
// Request/decision never consume approval, mint a continuation or invoke a provider/Workflow.
public sealed class DurableApprovalApplication(ExternalAdmissionInputs inputs, IPolicySource? policy,
    ITrustedIdentityResolver? identities, IExecutionStore executions,
    IApprovalTransactionCoordinator approvals, TimeProvider clock)
{
    void VerifyIdentity(ITrustedCallerContext? caller, bool human)
    {
        var code = human ? "OperatorNotAuthorized" : "InvalidCaller";
        try
        {
            var identity = caller is null ? null : (identities ?? UnavailableTrust.Instance).Resolve(caller);
            var now = clock.GetUtcNow();
            if (identity is null || identity.SubjectId != caller!.SubjectId || identity.AuthenticationContextId != caller.AuthenticationContextId
                || !Enum.IsDefined(identity.Kind) || identity.Kind == IdentityKind.Unknown || now < identity.NotBefore || now >= identity.NotAfter
                || human && (identity.Kind != IdentityKind.HumanOperator || !identity.FreshHumanPresence))
                throw new ExternalHarnessException(code);
        }
        catch (Exception) { throw new ExternalHarnessException(code); }
    }

    public async Task<ApprovalRecord> RequestAsync(string approvalId, ExternalAdmissionRequest intent,
        ITrustedCallerContext? caller, CancellationToken token = default)
    {
        VerifyIdentity(caller, human: false);
        if (intent.Mode != ExternalAdmissionMode.Submit || string.IsNullOrWhiteSpace(intent.TaskId) || intent.TaskId.Length > 100
            || intent.Timeout < TimeSpan.FromMilliseconds(10) || intent.Timeout > TimeSpan.FromMinutes(10))
            throw new ExternalHarnessException("DurableNewSubmissionRequired");
        await using var owner = await executions.TryAcquireAsync(intent.TaskId, token)
            ?? throw new ExternalHarnessException("TaskAlreadyOwned");
        if (await executions.GetAsync(intent.TaskId, token) is not null)
            throw new ExternalHarnessException("ExistingExecutionRecoveryOnly");
        var assessment = new ExternalExecutionApplication(inputs with { Executions = [] }, policy, identities, clock).Assess(intent, caller);
        if (assessment.Classification != ExternalAdmissionClassification.NewSubmission
            || assessment.Decision.Outcome != PolicyOutcome.RequireApproval || assessment.Operation is null)
            throw new ExternalHarnessException("ApprovalNotRequiredOrDenied");
        // Store reacquires trusted authority outside SQL, then verifies active policy, caller,
        // exact binding, uniqueness/control and required audit inside its existing transaction.
        return await approvals.RequestAsync(approvalId, assessment.Operation, caller!, token);
    }

    public Task<ApprovalRecord> DecideAsync(ApprovalDecisionCommand command, ITrustedCallerContext? actor,
        CancellationToken token = default)
    {
        VerifyIdentity(actor, human: true);
        // Authentication here is preliminary; the store separately reauthenticates and authorizes
        // the exact stored operation/operator scope, non-self decision and current CAS/control.
        return approvals.DecideAsync(command, actor!, token);
    }
}
