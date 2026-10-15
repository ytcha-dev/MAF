namespace MAF.Core.Policy;

/// <summary>
/// Pure M1 policy evaluation only. No Host integration, approval consumption, side effects, or identity resolver.
/// The injecting application must trust the source/context; calling this class does not authenticate a caller.
/// </summary>
public sealed class DeterministicPolicyEvaluator(IPolicySource? source, TimeProvider clock) : IPolicyEvaluator
{
    public PolicyDecision Evaluate(PolicyRequest request, ITrustedCallerContext caller) => EvaluateSafe(request, caller, null);
    public PolicyDecision Revalidate(PolicyRequest request, ITrustedCallerContext caller, PolicyDecision previous)
        => previous is null
            ? new(PolicyOutcome.Deny, PolicyReason.InvalidRequest, null, null, default, default, default, default)
            : EvaluateSafe(request, caller, previous);

    private PolicyDecision EvaluateSafe(PolicyRequest request, ITrustedCallerContext caller, PolicyDecision? previous)
    {
        DateTimeOffset now = default;
        PolicySnapshot? snapshot = null;
        PolicyDecision Deny(PolicyReason reason) => new(PolicyOutcome.Deny, reason, null,
            snapshot?.AuthorityId, snapshot?.Epoch ?? default, snapshot?.Version ?? default, now, now);
        try
        {
            now = clock.GetUtcNow();
            snapshot = source?.GetSnapshot();
            if (snapshot is null) return Deny(PolicyReason.PolicyUnavailable);
            if (!ValidSnapshot(snapshot, now)) return Deny(PolicyReason.InvalidPolicy);
            if (request is null || !Enum.IsDefined(request.Operation) || request.Operation == PolicyOperation.Unknown)
                return Deny(PolicyReason.UnknownOperation);
            if (!Enum.IsDefined(request.Capability) || request.Capability == PolicyCapability.Unknown)
                return Deny(PolicyReason.UnknownCapability);
            if (!PolicyValidation.Request(request)) return Deny(PolicyReason.InvalidRequest);
            var subject = caller?.SubjectId;
            var authenticationContext = caller?.AuthenticationContextId;
            if (!PolicyValidation.Text(subject) || !PolicyValidation.Text(authenticationContext))
                return Deny(PolicyReason.InvalidCaller);
            if (subject != request.CallerSubject) return Deny(PolicyReason.CallerMismatch);
            if (request.AuthorityId != snapshot.AuthorityId) return Deny(PolicyReason.AuthorityMismatch);
            if (request.Epoch != snapshot.Epoch) return Deny(PolicyReason.PolicyEpochMismatch);
            if (request.Version != snapshot.Version) return Deny(PolicyReason.PolicyVersionMismatch);
            if (!PolicyValidation.Supported(request.Operation) || request.Scope.Access != ResourceAccess.ReadOnly)
                return Deny(PolicyReason.NotSupported);
            var descriptor = snapshot.Capabilities.SingleOrDefault(c => c.ConnectorId == request.ConnectorId && c.Capability == request.Capability);
            if (descriptor is null) return Deny(PolicyReason.UnknownCapability);
            if (descriptor.Boundary != EnforcementBoundary.TaskAdmission || !descriptor.SupportedOperations.Contains(request.Operation)
                || request.Capability != PolicyValidation.CapabilityFor(request.Operation)) return Deny(PolicyReason.NotSupported);
            if (!descriptor.Enabled || !descriptor.Healthy) return Deny(PolicyReason.ConnectorUnavailable);
            var candidates = snapshot.Rules.Where(r => r.CallerSubject == subject && r.ConnectorId == request.ConnectorId
                && r.Capability == request.Capability && r.Operation == request.Operation).ToArray();
            var matches = candidates.Where(r => r.Scope == request.Scope).ToArray();
            if (matches.Length == 0) return Deny(candidates.Length == 0 ? PolicyReason.NoMatchingRule : PolicyReason.ScopeMismatch);
            if (matches.Any(r => r.Outcome == PolicyOutcome.Deny)) return Deny(PolicyReason.ExplicitDeny);
            // The most restrictive matching outcome wins, independent of rule order.
            var approval = matches.Where(r => r.Outcome == PolicyOutcome.RequireApproval).ToArray();
            if (approval.Select(r => r.RequiredOperatorScope).Distinct(StringComparer.Ordinal).Count() > 1)
                return Deny(PolicyReason.InvalidPolicy);
            var outcome = approval.Length > 0 ? PolicyOutcome.RequireApproval : PolicyOutcome.Allow;
            var digest = OperationBinding.Digest(request, descriptor);
            if (previous is not null)
            {
                if (previous.AuthorityId != snapshot.AuthorityId) return Deny(PolicyReason.AuthorityMismatch);
                if (previous.Epoch != snapshot.Epoch) return Deny(PolicyReason.PolicyEpochMismatch);
                if (previous.Version != snapshot.Version) return Deny(PolicyReason.PolicyVersionMismatch);
                if (now < previous.EvaluatedAt) return Deny(PolicyReason.DecisionNotYetValid);
                if (now >= previous.NotAfter || previous.NotAfter <= previous.EvaluatedAt) return Deny(PolicyReason.DecisionExpired);
                if (previous.BindingDigest != digest) return Deny(PolicyReason.BindingMismatch);
                // No approval object is accepted in M1; RequireApproval can never become Allow by replaying a decision.
                if (previous.Outcome != PolicyOutcome.Allow || previous.Reason != PolicyReason.Allowed)
                    return Deny(PolicyReason.ApprovalRequired);
            }
            var deadline = now + snapshot.DecisionLifetime;
            if (deadline > snapshot.NotAfter) deadline = snapshot.NotAfter;
            // Revalidation cannot indefinitely extend the original decision's lifetime.
            if (previous is not null && deadline > previous.NotAfter) deadline = previous.NotAfter;
            return new(outcome, outcome == PolicyOutcome.Allow ? PolicyReason.Allowed : PolicyReason.ApprovalRequired,
                digest, snapshot.AuthorityId, snapshot.Epoch, snapshot.Version, now, deadline, approval.FirstOrDefault()?.RequiredOperatorScope);
        }
        catch (Exception)
        {
            // Neither source/context/clock failures nor malformed data expose exception text or fail open.
            return Deny(PolicyReason.EvaluationError);
        }
    }

    private static bool ValidSnapshot(PolicySnapshot s, DateTimeOffset now)
    {
        if (!PolicyValidation.Text(s.AuthorityId) || !s.Epoch.IsValid || !s.Version.IsValid || now < s.NotBefore || now >= s.NotAfter
            || s.NotBefore >= s.NotAfter || s.DecisionLifetime <= TimeSpan.Zero || s.DecisionLifetime > TimeSpan.FromHours(1)
            || s.Capabilities.IsDefault || s.Rules.IsDefault || s.Capabilities.Length > 100 || s.Rules.Length > 1000) return false;
        if (s.Capabilities.Any(c => c is null || !PolicyValidation.Text(c.ConnectorId) || !PolicyValidation.Text(c.AdapterVersion)
            || !Enum.IsDefined(c.Capability) || c.Capability == PolicyCapability.Unknown || !Enum.IsDefined(c.Boundary)
            || c.Boundary == EnforcementBoundary.Unknown || c.SupportedOperations.IsDefaultOrEmpty
            || c.SupportedOperations.Any(op => !Enum.IsDefined(op) || op == PolicyOperation.Unknown)
            || c.SupportedOperations.Distinct().Count() != c.SupportedOperations.Length)) return false;
        if (s.Capabilities.Select(c => (c.ConnectorId, c.Capability)).Distinct().Count() != s.Capabilities.Length) return false;
        return s.Rules.All(r => r is not null && PolicyValidation.Text(r.CallerSubject) && PolicyValidation.Text(r.ConnectorId)
            && Enum.IsDefined(r.Capability) && r.Capability != PolicyCapability.Unknown && Enum.IsDefined(r.Operation)
            && r.Operation != PolicyOperation.Unknown && PolicyValidation.Scope(r.Scope) && Enum.IsDefined(r.Outcome)
            && (r.Outcome != PolicyOutcome.RequireApproval || PolicyValidation.Text(r.RequiredOperatorScope)));
    }
}
