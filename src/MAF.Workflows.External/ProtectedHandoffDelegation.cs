using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Workflows.External;

// Internal controlled composition only. The outer filter is never an authorization grant.
internal sealed class ProtectedHandoffDelegation(ProtectedExternalRuntime runtime, ITrustedCallerContext caller,
    ExternalTaskRequest original, string originalRunId, ExternalExecutionHandle originalHandle,
    ExternalAdmissionMode mode = ExternalAdmissionMode.Submit) : IControlledDelegation
{
    internal ExternalWorkflowRun? InnerRun { get; private set; }
    internal Action? BeforeHandoff { get; init; }

    public async Task<ExternalTaskResult> DelegateAsync(ExternalTaskRequest request, string workflowRunId, CancellationToken token)
    {
        if (mode is not (ExternalAdmissionMode.Submit or ExternalAdmissionMode.Inspect)
            || workflowRunId != originalRunId || request != original || original.Policy != ExternalExecutionPolicy.ReadOnly
            || originalHandle.ExternalSessionId is not null)
            throw new ExternalHarnessException("ProtectedHandoffBindingMismatch");
        // Runtime independently authenticates, acquires ownership, re-reads storage, admits or
        // inspects, and authorizes each effect. No continuation or authority escapes that scope.
        var run = await runtime.RunAsync(new(mode, original.TaskId, original.Timeout,
            mode == ExternalAdmissionMode.Submit ? original : null), caller, observeOnce: true, token: token);
        var result = run.Result;
        if (result.TaskId != original.TaskId || result.WorkflowRunId != originalRunId
            || result.Handle.ExecutionId != originalHandle.ExecutionId || result.Handle.Provider != originalHandle.Provider
            || result.Handle.ExternalRequestId != originalHandle.ExternalRequestId
            || result.Status == ExternalExecutionStatus.Completed && string.IsNullOrWhiteSpace(result.Handle.ExternalSessionId))
            throw new ExternalHarnessException("ProtectedHandoffIdentityMismatch");
        InnerRun = run;
        BeforeHandoff?.Invoke(); // Test-owned stage-boundary interruption, after child ownership ends.
        return result;
    }
}
