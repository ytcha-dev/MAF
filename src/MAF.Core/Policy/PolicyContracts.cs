using System.Collections.Immutable;

namespace MAF.Core.Policy;

public readonly record struct PolicyEpoch(long Value) { public bool IsValid => Value > 0; }
public readonly record struct PolicyVersion(long Value) { public bool IsValid => Value > 0; }
public enum PolicyOutcome { Deny, Allow, RequireApproval }
public enum PolicyOperation
{
    Unknown, SubmitNewReadOnlyTask, InspectExistingExecution, RequestCancelExecution,
    ResolveProviderPermission, WriteWorkspace, ExecuteShell, ElevateScope
}
public enum PolicyCapability { Unknown, ReadOnlyTask, ExecutionInspection, ExecutionCancellation }
public enum EnforcementBoundary { Unknown, TaskAdmission, ProviderChallenge }
public enum ResourceAccess { Unknown, ReadOnly, Write, Shell }
public enum PolicyReason
{
    Allowed, ExplicitDeny, ApprovalRequired, NoMatchingRule, InvalidPolicy, PolicyUnavailable,
    InvalidRequest, InvalidCaller, CallerMismatch, UnknownOperation, UnknownCapability, NotSupported,
    ConnectorUnavailable, ScopeMismatch, PolicyEpochMismatch, PolicyVersionMismatch, AuthorityMismatch,
    DecisionExpired, DecisionNotYetValid, BindingMismatch, EvaluationError
}

/// <summary>
/// Supplied by a future trusted application boundary, never from request JSON or environment actor names.
/// This contract does not authenticate humans or distinguish agents sharing the same OS principal.
/// M1 deliberately provides no production implementation, resolver, or fallback.
/// </summary>
public interface ITrustedCallerContext
{
    string SubjectId { get; }
    string AuthenticationContextId { get; }
}

// ResourceId is an opaque, already canonical resource identifier. No filesystem IO occurs here.
// A future integration must validate actual paths/reparse points before constructing this scope.
public sealed record AuthorizationScope(string ResourceId, string ProviderScopeDigest, string AgentPolicyDigest,
    long ConfigurationRevision, long ScopeRevision, ResourceAccess Access, long BudgetTicks);

public sealed record CapabilityDescriptor(string ConnectorId, string AdapterVersion, PolicyCapability Capability,
    EnforcementBoundary Boundary, ImmutableArray<PolicyOperation> SupportedOperations, bool Enabled, bool Healthy);

public sealed record PolicyRequest(string AuthorityId, string CallerSubject, string OperationId, string TaskId,
    string WorkflowRunId, string ExecutionId, string MessageId, string RequestFingerprint,
    string ConnectorId, PolicyCapability Capability, PolicyOperation Operation, AuthorizationScope Scope,
    PolicyEpoch Epoch, PolicyVersion Version, long IntentGeneration = 1, string? ProviderSessionId = null);

public sealed record PolicyDecision(PolicyOutcome Outcome, PolicyReason Reason, string? BindingDigest,
    string? AuthorityId, PolicyEpoch Epoch, PolicyVersion Version, DateTimeOffset EvaluatedAt,
    DateTimeOffset NotAfter, string? RequiredOperatorScope = null);

// Rules are exact matches; no wildcards, ambient identity, LLM decisions, or connector registry.
public sealed record PolicyRule(string CallerSubject, string ConnectorId, PolicyCapability Capability,
    PolicyOperation Operation, AuthorizationScope Scope, PolicyOutcome Outcome, string? RequiredOperatorScope = null);

public sealed record PolicySnapshot(string AuthorityId, PolicyEpoch Epoch, PolicyVersion Version,
    DateTimeOffset NotBefore, DateTimeOffset NotAfter, TimeSpan DecisionLifetime,
    ImmutableArray<CapabilityDescriptor> Capabilities, ImmutableArray<PolicyRule> Rules);

public interface IPolicySource { PolicySnapshot? GetSnapshot(); }

/// <summary>Explicit immutable configuration, not a persistent policy authority or authentication provider.</summary>
public sealed class ImmutablePolicySource(PolicySnapshot snapshot) : IPolicySource
{
    public PolicySnapshot GetSnapshot() => snapshot;
}

public interface IPolicyEvaluator
{
    PolicyDecision Evaluate(PolicyRequest request, ITrustedCallerContext caller);
    // Re-evaluate against current trusted configuration; a previous decision is not a bearer grant.
    PolicyDecision Revalidate(PolicyRequest request, ITrustedCallerContext caller, PolicyDecision previous);
}

internal static class PolicyValidation
{
    internal static bool Text(string? value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 1024
        && !value.Any(char.IsControl) && WellFormedUtf16(value);
    private static bool WellFormedUtf16(string value)
    {
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i])) { if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false; }
            else if (char.IsLowSurrogate(value[i])) return false;
        }
        return true;
    }
    internal static bool Hash(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    internal static bool Scope(AuthorizationScope? s) => s is not null && Text(s.ResourceId)
        && Hash(s.ProviderScopeDigest) && Hash(s.AgentPolicyDigest) && s.ConfigurationRevision > 0 && s.ScopeRevision > 0
        && Enum.IsDefined(s.Access) && s.Access != ResourceAccess.Unknown && s.BudgetTicks > 0;
    internal static bool Supported(PolicyOperation op) => op is PolicyOperation.SubmitNewReadOnlyTask
        or PolicyOperation.InspectExistingExecution or PolicyOperation.RequestCancelExecution;
    internal static PolicyCapability CapabilityFor(PolicyOperation op) => op switch
    {
        PolicyOperation.SubmitNewReadOnlyTask => PolicyCapability.ReadOnlyTask,
        PolicyOperation.InspectExistingExecution => PolicyCapability.ExecutionInspection,
        PolicyOperation.RequestCancelExecution => PolicyCapability.ExecutionCancellation,
        _ => PolicyCapability.Unknown
    };
    internal static bool Request(PolicyRequest? r) => r is not null && Text(r.AuthorityId) && Text(r.CallerSubject)
        && Text(r.OperationId) && Text(r.TaskId) && Text(r.WorkflowRunId) && Text(r.ExecutionId) && Text(r.MessageId)
        && Hash(r.RequestFingerprint) && Text(r.ConnectorId) && Scope(r.Scope) && r.Epoch.IsValid && r.Version.IsValid
        && r.IntentGeneration > 0 && (r.ProviderSessionId is null || Text(r.ProviderSessionId));
}
