namespace MAF.Core.Policy;

// Metadata only; never a dispatch/Abort permission. Production composition remains unavailable.
public sealed record CancellationCommand(string CancelRequestId, string TaskId, string ExecutionId, long ExpectedControlVersion);
public sealed record CancellationAdmissionInputs(string AuthorityId, PolicyEpoch Epoch, PolicyVersion Version,
    AuthorizationScope Scope, CapabilityDescriptor Capability, string OperationId);
public enum CancellationAdmissionStatus { Recorded, AlreadyRecorded, AlreadyTerminal, LegacyIntentUnverified }
public sealed record CancellationEvidence(string CancelRequestId, PolicyRequest Operation, CapabilityDescriptor Capability,
    string AuthenticationContextId, string? TargetOperationId, string PolicyDigest, string BindingDigest,
    long ExpectedControlVersion, long CommittedControlVersion, DateTimeOffset EvaluatedAt,
    DateTimeOffset ExpiresAt, DateTimeOffset RequestedAt);
public sealed record CancellationAdmissionResult(CancellationAdmissionStatus Status, CancellationEvidence? Evidence);

public interface ICancellationAdmissionCoordinator
{
    Task<CancellationAdmissionResult> RequestCancelAsync(CancellationCommand command, CancellationAdmissionInputs inputs, ITrustedCallerContext caller, CancellationToken token = default);
    // Trusted storage read only, not an authenticated metadata ingress or executable grant.
    Task<CancellationEvidence?> GetCancellationAsync(string cancelRequestId, CancellationToken token = default);
}
