using System.Collections.Immutable;
using System.Text.Json.Serialization;
using MAF.Core;
using MAF.Core.Policy;

namespace MAF.Workflows.External;

public enum ExternalAdmissionMode { Unknown, Submit, EphemeralSubmit, Inspect, BatchInspect, Cancel }
public enum ExternalAdmissionClassification { Unclassified, NewSubmission, EphemeralSubmission, ExistingInspection, BatchInspection }

// Untrusted intent only: no caller subject, authority, epoch, capability or scope claims.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExternalAdmissionRequest(ExternalAdmissionMode Mode, string TaskId, TimeSpan Timeout,
    ExternalTaskRequest? Submission = null);

// An immutable, provider-free snapshot supplied by trusted application composition, NOT ingress
// JSON/repository configuration. Scope.ResourceId must be a previously verified canonical root;
// Scope.BudgetTicks is the maximum request budget. This class does no filesystem/DB verification.
// Preallocated IDs are metadata, not a provider session or a continuation. Existing executions
// are advisory snapshots; M3-A2 must re-read them under ownership before any real admission.
public sealed record ExternalAdmissionInputs(string AuthorityId, PolicyEpoch Epoch, PolicyVersion Version,
    string ProviderId, AuthorizationScope Scope, CapabilityDescriptor SubmissionCapability,
    CapabilityDescriptor InspectionCapability, string OperationId, string NewWorkflowRunId,
    ExternalExecutionHandle PreparedHandle, ImmutableArray<DurableExecution> Executions);

// A non-executable assessment. Neither this record nor its PolicyDecision is a dispatch ticket.
public sealed record ExternalAdmissionResult(ExternalAdmissionClassification Classification,
    PolicyDecision Decision, PolicyRequest? Operation = null);

/// <summary>
/// M3-A1 assessment only. No Host registration, activation, storage mutations, workflow/client
/// dependencies or execution callbacks. Trust provenance belongs to the injecting application.
/// </summary>
public sealed class ExternalExecutionApplication
{
    readonly ExternalAdmissionInputs inputs;
    readonly IPolicySource? policy;
    readonly ITrustedIdentityResolver identities;
    readonly TimeProvider clock;

    public ExternalExecutionApplication(ExternalAdmissionInputs inputs, IPolicySource? policy,
        ITrustedIdentityResolver? identities, TimeProvider clock)
    {
        this.inputs = inputs ?? throw new ArgumentNullException(nameof(inputs));
        this.policy = policy;
        this.identities = identities ?? UnavailableTrust.Instance;
        this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public ExternalAdmissionResult Assess(ExternalAdmissionRequest request, ITrustedCallerContext? context)
    {
        var classification = ExternalAdmissionClassification.Unclassified;
        DateTimeOffset now = default;
        ExternalAdmissionResult Deny(PolicyReason reason) => new(classification,
            new(PolicyOutcome.Deny, reason, null, null, default, default, now, now));
        try
        {
            now = clock.GetUtcNow();
            if (context is null) return Deny(PolicyReason.InvalidCaller);
            // Freeze authenticated metadata; an interface implementation or matching strings alone
            // is not identity verification. The resolver must verify the original context's origin.
            var subject = context.SubjectId; var authentication = context.AuthenticationContextId;
            var identity = identities.Resolve(context);
            if (identity is null || identity.SubjectId != subject || identity.AuthenticationContextId != authentication
                || !Enum.IsDefined(identity.Kind) || identity.Kind == IdentityKind.Unknown
                || now < identity.NotBefore || now >= identity.NotAfter)
                return Deny(PolicyReason.InvalidCaller);
            if (request is null || !Enum.IsDefined(request.Mode) || request.Mode == ExternalAdmissionMode.Unknown)
                return Deny(PolicyReason.UnknownOperation);
            if (request.Mode == ExternalAdmissionMode.BatchInspect)
            {
                classification = ExternalAdmissionClassification.BatchInspection;
                // No trusted list authorization contract exists. Do not enumerate/disclose snapshots
                // or infer global list permission from one execution's inspection policy.
                return Deny(PolicyReason.NotSupported);
            }
            if (request.Mode == ExternalAdmissionMode.Cancel) return Deny(PolicyReason.NotSupported);
            if (string.IsNullOrWhiteSpace(request.TaskId) || request.TaskId.Length > 100
                || request.Timeout < TimeSpan.FromMilliseconds(10) || request.Timeout > TimeSpan.FromMinutes(10)
                || inputs.Executions.IsDefault || inputs.Executions.Length > 100)
                return Deny(PolicyReason.InvalidRequest);

            var root = CanonicalRoot(inputs.Scope.ResourceId);
            if (root is null || root != inputs.Scope.ResourceId || request.Timeout.Ticks > inputs.Scope.BudgetTicks)
                return Deny(PolicyReason.ScopeMismatch);
            var scope = inputs.Scope with { ResourceId = root, BudgetTicks = request.Timeout.Ticks };
            var submission = request.Submission;
            var submitting = request.Mode is ExternalAdmissionMode.Submit or ExternalAdmissionMode.EphemeralSubmit;
            if (submitting)
            {
                if (submission is null || submission.TaskId != request.TaskId || submission.Timeout != request.Timeout
                    || string.IsNullOrWhiteSpace(submission.TaskText) || submission.TaskText.Length > 4000)
                    return Deny(PolicyReason.InvalidRequest);
                if (submission.Policy != ExternalExecutionPolicy.ReadOnly || scope.Access != ResourceAccess.ReadOnly)
                    return Deny(PolicyReason.NotSupported);
                if (CanonicalRoot(submission.ProjectRoot) != root) return Deny(PolicyReason.ScopeMismatch);
            }
            else if (submission is not null) return Deny(PolicyReason.InvalidRequest);

            var existing = inputs.Executions.SingleOrDefault(e => e.TaskId == request.TaskId);
            var operation = PolicyOperation.SubmitNewReadOnlyTask;
            var descriptor = inputs.SubmissionCapability;
            var run = inputs.NewWorkflowRunId; var handle = inputs.PreparedHandle;
            string fingerprint;
            if (existing is not null)
            {
                classification = ExternalAdmissionClassification.ExistingInspection;
                if (existing.ProjectRoot != root || existing.ProviderScope != scope.ProviderScopeDigest)
                    return Deny(PolicyReason.ScopeMismatch);
                if (submitting && DurableExecution.Fingerprint(submission!) != existing.RequestFingerprint)
                    return Deny(PolicyReason.BindingMismatch);
                operation = PolicyOperation.InspectExistingExecution; descriptor = inputs.InspectionCapability;
                run = existing.WorkflowRunId; handle = existing.Handle; fingerprint = existing.RequestFingerprint;
            }
            else
            {
                if (!submitting) return Deny(PolicyReason.InvalidRequest);
                classification = request.Mode == ExternalAdmissionMode.EphemeralSubmit
                    ? ExternalAdmissionClassification.EphemeralSubmission : ExternalAdmissionClassification.NewSubmission;
                if (handle.ExternalSessionId is not null) return Deny(PolicyReason.BindingMismatch);
                fingerprint = DurableExecution.Fingerprint(submission!);
            }
            if (handle.Provider != inputs.ProviderId) return Deny(PolicyReason.BindingMismatch);
            var bound = new PolicyRequest(inputs.AuthorityId, identity.SubjectId, inputs.OperationId, request.TaskId,
                run, handle.ExecutionId, handle.ExternalRequestId!, fingerprint, descriptor.ConnectorId,
                descriptor.Capability, operation, scope, inputs.Epoch, inputs.Version, ProviderSessionId: handle.ExternalSessionId);
            // One immutable policy snapshot and evaluation instant per assessment. No authority
            // Acquire/Pin/Activate or implicit policy publication is performed here.
            var evaluator = new DeterministicPolicyEvaluator(policy, new AssessmentClock(now));
            var decision = evaluator.Evaluate(bound, new VerifiedCaller(identity.SubjectId, identity.AuthenticationContextId));
            if (decision.Outcome != PolicyOutcome.Deny && decision.BindingDigest != OperationBinding.Digest(bound, descriptor))
                return Deny(PolicyReason.BindingMismatch); // Policy descriptor must match the application binding too.
            if (decision.Outcome == PolicyOutcome.Deny) return new(classification, decision);
            if (decision.NotAfter > identity.NotAfter) decision = decision with { NotAfter = identity.NotAfter };
            return new(classification, decision, bound);
        }
        catch (Exception)
        {
            // Dependency/clock/malformed snapshot failures are non-executable and reveal no text.
            return Deny(PolicyReason.EvaluationError);
        }
    }

    static string? CanonicalRoot(string root)
    {
        try { return Path.IsPathFullyQualified(root) ? DurableExecution.NormalizeRoot(root) : null; }
        catch (Exception error) when (error is ArgumentException or IOException or NotSupportedException) { return null; }
    }
    sealed record VerifiedCaller(string SubjectId, string AuthenticationContextId) : ITrustedCallerContext;
    sealed class AssessmentClock(DateTimeOffset now) : TimeProvider { public override DateTimeOffset GetUtcNow() => now; }
}
