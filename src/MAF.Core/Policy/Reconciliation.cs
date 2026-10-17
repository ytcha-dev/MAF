namespace MAF.Core.Policy;

public sealed record ReconciliationInputs(string AuthorityId, PolicyEpoch Epoch, PolicyVersion Version,
    AuthorizationScope Scope, CapabilityDescriptor Capability, string OperationId);
public sealed record ReconciliationResult(ExternalExecutionStatus Status, bool Recorded, string Diagnostic, string? TerminalDigest = null);
public interface IReconciliationCoordinator
{
    Task<ReconciliationSession> BeginReconciliationAsync(string deliveryId, string taskId, ReconciliationInputs inputs,
        ITrustedCallerContext caller, IExecutionOwnership owner, CancellationToken token = default);
}
// Provider-neutral projection issued only by the original protected adapter observation.
// Not a public deserializable authorization/evidence input and never carries model output.
internal sealed record ObservedTerminal(string ProviderId, string ProviderScope, string SessionId, string OriginalMessageId,
    string ObservedMessageId, string ParentMessageId, string? FinishReason, string? ErrorCode, ExternalExecutionStatus Outcome, long ObservedAtTicks);

public sealed class ReconciliationSession
{
    internal readonly DurableExecution Execution;
    internal readonly long ControlVersion;
    readonly Func<ExternalObservation, CancellationToken, Task<ReconciliationResult>> capture;
    internal ReconciliationSession(DurableExecution execution, long controlVersion,
        Func<ExternalObservation, CancellationToken, Task<ReconciliationResult>> capture)
    { Execution = execution; ControlVersion = controlVersion; this.capture = capture; }
    internal Task<ReconciliationResult> Capture(ExternalObservation observation, CancellationToken token) => capture(observation, token);
}
