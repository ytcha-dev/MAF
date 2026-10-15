using System.Collections.Immutable;
using System.Text.Json;
using MAF.Core.Policy;

namespace MAF.Tests;

public sealed class PolicyTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    static readonly AuthorizationScope Scope = new("resource:synthetic", new('A', 64), new('B', 64), 1, 1,
        ResourceAccess.ReadOnly, TimeSpan.FromSeconds(30).Ticks);
    static readonly CapabilityDescriptor Descriptor = new("synthetic-connector", "adapter-v1", PolicyCapability.ReadOnlyTask,
        EnforcementBoundary.TaskAdmission, [PolicyOperation.SubmitNewReadOnlyTask], true, true);
    static readonly PolicyRequest Request = new("authority", "subject", "operation", "task", "run", "execution", "message",
        new('C', 64), Descriptor.ConnectorId, Descriptor.Capability, PolicyOperation.SubmitNewReadOnlyTask, Scope, new(1), new(1));
    // Test-only identity; not an implementation of production operator authentication.
    sealed record SyntheticCaller(string SubjectId = "subject", string AuthenticationContextId = "synthetic-context") : ITrustedCallerContext;
    sealed class FakeClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = PolicyTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    sealed class BrokenSource : IPolicySource { public PolicySnapshot GetSnapshot() => throw new Exception("secret exception marker"); }
    sealed class BrokenCaller : ITrustedCallerContext
    { public string SubjectId => throw new Exception("secret identity marker"); public string AuthenticationContextId => "unused"; }
    sealed class BrokenClock : TimeProvider { public override DateTimeOffset GetUtcNow() => throw new Exception("secret clock marker"); }
    static PolicyRule Rule(PolicyOutcome outcome = PolicyOutcome.Allow) => new("subject", Descriptor.ConnectorId,
        Descriptor.Capability, Request.Operation, Scope, outcome, outcome == PolicyOutcome.RequireApproval ? "operator:read-only" : null);
    static PolicySnapshot Snapshot(params PolicyRule[] rules) => new("authority", new(1), new(1), Now.AddHours(-1), Now.AddHours(1),
        TimeSpan.FromMinutes(5), [Descriptor], [..rules]);
    static DeterministicPolicyEvaluator Evaluator(PolicySnapshot? snapshot = null, TimeProvider? clock = null)
        => new(new ImmutablePolicySource(snapshot ?? Snapshot(Rule())), clock ?? new FakeClock());

    [Theory]
    [InlineData(PolicyOutcome.Allow, PolicyReason.Allowed)]
    [InlineData(PolicyOutcome.Deny, PolicyReason.ExplicitDeny)]
    [InlineData(PolicyOutcome.RequireApproval, PolicyReason.ApprovalRequired)]
    public void ExactRuleProducesExpectedDecision(PolicyOutcome outcome, PolicyReason reason)
    {
        var decision = Evaluator(Snapshot(Rule(outcome))).Evaluate(Request, new SyntheticCaller());
        Assert.Equal(outcome, decision.Outcome); Assert.Equal(reason, decision.Reason);
        if (outcome != PolicyOutcome.Deny)
        {
            Assert.Equal(OperationBinding.Digest(Request, Descriptor), decision.BindingDigest);
            Assert.Equal(Now.AddMinutes(5), decision.NotAfter);
            Assert.Equal(outcome == PolicyOutcome.RequireApproval ? "operator:read-only" : null, decision.RequiredOperatorScope);
        }
    }

    [Theory]
    [InlineData(PolicyOperation.SubmitNewReadOnlyTask, PolicyCapability.ReadOnlyTask)]
    [InlineData(PolicyOperation.InspectExistingExecution, PolicyCapability.ExecutionInspection)]
    [InlineData(PolicyOperation.RequestCancelExecution, PolicyCapability.ExecutionCancellation)]
    public void AllThreeSupportedOperationsRequireTheirOwnExplicitRule(PolicyOperation operation, PolicyCapability capability)
    {
        var descriptor = Descriptor with { Capability = capability, SupportedOperations = [operation] };
        var snapshot = Snapshot(Rule() with { Capability = capability, Operation = operation }) with { Capabilities = [descriptor] };
        Assert.Equal(PolicyOutcome.Allow, Evaluator(snapshot).Evaluate(Request with { Operation = operation, Capability = capability }, new SyntheticCaller()).Outcome);
        Assert.Equal(PolicyOutcome.Deny, Evaluator(snapshot with { Rules = [] }).Evaluate(Request with { Operation = operation, Capability = capability }, new SyntheticCaller()).Outcome);
    }

    [Theory]
    [InlineData(PolicyOperation.Unknown, PolicyReason.UnknownOperation)]
    [InlineData((PolicyOperation)999, PolicyReason.UnknownOperation)]
    [InlineData(PolicyOperation.ResolveProviderPermission, PolicyReason.NotSupported)]
    [InlineData(PolicyOperation.WriteWorkspace, PolicyReason.NotSupported)]
    [InlineData(PolicyOperation.ExecuteShell, PolicyReason.NotSupported)]
    [InlineData(PolicyOperation.ElevateScope, PolicyReason.NotSupported)]
    public void UnknownOrHighRiskOperationNeverBecomesAllow(PolicyOperation operation, PolicyReason reason)
    {
        var decision = Evaluator().Evaluate(Request with { Operation = operation }, new SyntheticCaller());
        Assert.Equal(PolicyOutcome.Deny, decision.Outcome); Assert.Equal(reason, decision.Reason);
    }

    [Theory]
    [InlineData(PolicyCapability.Unknown)]
    [InlineData((PolicyCapability)999)]
    public void UnknownCapabilitiesAreDenied(PolicyCapability capability)
        => Assert.Equal(PolicyReason.UnknownCapability, Evaluator().Evaluate(Request with { Capability = capability }, new SyntheticCaller()).Reason);

    [Theory]
    [InlineData(PolicyOperation.ResolveProviderPermission)]
    [InlineData(PolicyOperation.WriteWorkspace)]
    [InlineData(PolicyOperation.ExecuteShell)]
    [InlineData(PolicyOperation.ElevateScope)]
    public void ExplicitRulesCannotEnableUnsupportedOperations(PolicyOperation operation)
    {
        var snapshot = Snapshot(Rule() with { Operation = operation }) with { Capabilities = [Descriptor with { SupportedOperations = [operation] }] };
        var result = Evaluator(snapshot).Evaluate(Request with { Operation = operation }, new SyntheticCaller());
        Assert.Equal(PolicyOutcome.Deny, result.Outcome); Assert.Equal(PolicyReason.NotSupported, result.Reason);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void ConnectorAvailabilityIsNecessaryButNotAuthorization(bool enabled, bool healthy)
    {
        var snapshot = Snapshot(Rule()) with { Capabilities = [Descriptor with { Enabled = enabled, Healthy = healthy }] };
        Assert.Equal(PolicyReason.ConnectorUnavailable, Evaluator(snapshot).Evaluate(Request, new SyntheticCaller()).Reason);
        Assert.Equal(PolicyReason.NoMatchingRule, Evaluator(Snapshot()).Evaluate(Request, new SyntheticCaller()).Reason);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void DenyThenRequireApprovalWinRegardlessOfRuleOrder(bool reverse)
    {
        var rules = new[] { Rule(), Rule(PolicyOutcome.RequireApproval), Rule(PolicyOutcome.Deny) };
        if (reverse) Array.Reverse(rules);
        Assert.Equal(PolicyReason.ExplicitDeny, Evaluator(Snapshot(rules)).Evaluate(Request, new SyntheticCaller()).Reason);
        Assert.Equal(PolicyOutcome.RequireApproval, Evaluator(Snapshot(rules.Where(r => r.Outcome != PolicyOutcome.Deny).ToArray())).Evaluate(Request, new SyntheticCaller()).Outcome);
    }

    public static IEnumerable<object[]> InvalidRequests()
    {
        yield return [Request with { CallerSubject = "" }, PolicyReason.InvalidRequest];
        yield return [Request with { TaskId = "" }, PolicyReason.InvalidRequest];
        yield return [Request with { RequestFingerprint = "not-a-hash" }, PolicyReason.InvalidRequest];
        yield return [Request with { IntentGeneration = 0 }, PolicyReason.InvalidRequest];
        yield return [Request with { Epoch = default }, PolicyReason.InvalidRequest];
        yield return [Request with { Version = default }, PolicyReason.InvalidRequest];
        yield return [Request with { Scope = null! }, PolicyReason.InvalidRequest];
        yield return [Request with { Scope = Scope with { Access = (ResourceAccess)999 } }, PolicyReason.InvalidRequest];
        yield return [Request with { Scope = Scope with { Access = ResourceAccess.Write } }, PolicyReason.NotSupported];
        yield return [Request with { Scope = Scope with { Access = ResourceAccess.Shell } }, PolicyReason.NotSupported];
        yield return [Request with { Scope = Scope with { ResourceId = "elsewhere" } }, PolicyReason.ScopeMismatch];
        yield return [Request with { Scope = Scope with { BudgetTicks = 1 } }, PolicyReason.ScopeMismatch];
        yield return [Request with { Scope = Scope with { ConfigurationRevision = 2 } }, PolicyReason.ScopeMismatch];
        yield return [Request with { Scope = Scope with { ProviderScopeDigest = new('D', 64) } }, PolicyReason.ScopeMismatch];
        yield return [Request with { AuthorityId = "other" }, PolicyReason.AuthorityMismatch];
        yield return [Request with { Epoch = new(2) }, PolicyReason.PolicyEpochMismatch];
        yield return [Request with { Version = new(2) }, PolicyReason.PolicyVersionMismatch];
        yield return [Request with { CallerSubject = "forged" }, PolicyReason.CallerMismatch];
    }
    [Theory, MemberData(nameof(InvalidRequests))]
    public void InvalidOrMismatchedRequestsFailClosed(PolicyRequest request, PolicyReason reason)
    {
        var decision = Evaluator().Evaluate(request, new SyntheticCaller());
        Assert.Equal(PolicyOutcome.Deny, decision.Outcome); Assert.Equal(reason, decision.Reason);
    }

    public static IEnumerable<object[]> InvalidPolicies()
    {
        var s = Snapshot(Rule());
        yield return [s with { Epoch = default }]; yield return [s with { Version = default }];
        yield return [s with { AuthorityId = "" }]; yield return [s with { NotAfter = Now }];
        yield return [s with { NotBefore = Now.AddSeconds(1) }]; yield return [s with { DecisionLifetime = TimeSpan.Zero }];
        yield return [s with { Rules = default }]; yield return [s with { Capabilities = default }];
        yield return [s with { Capabilities = [Descriptor, Descriptor] }];
        yield return [s with { Capabilities = [Descriptor with { SupportedOperations = [(PolicyOperation)999] }] }];
        yield return [s with { Capabilities = [Descriptor with { Boundary = (EnforcementBoundary)999 }] }];
        yield return [s with { Rules = [Rule() with { Outcome = (PolicyOutcome)999 }] }];
        yield return [s with { Rules = [Rule(PolicyOutcome.RequireApproval) with { RequiredOperatorScope = null }] }];
        yield return [s with { Rules = [null!] }];
    }
    [Theory, MemberData(nameof(InvalidPolicies))]
    public void InvalidPolicyDoesNotAuthorize(PolicySnapshot snapshot)
    {
        var result = Evaluator(snapshot).Evaluate(Request, new SyntheticCaller());
        Assert.Equal(PolicyOutcome.Deny, result.Outcome); Assert.Equal(PolicyReason.InvalidPolicy, result.Reason);
    }

    [Fact]
    public void MissingPolicyAndCallerNeverFallBackToAuthentication()
    {
        Assert.Equal(PolicyReason.PolicyUnavailable, new DeterministicPolicyEvaluator(null, new FakeClock()).Evaluate(Request, new SyntheticCaller()).Reason);
        Assert.Equal(PolicyReason.InvalidCaller, Evaluator().Evaluate(Request, null!).Reason);
        Assert.Equal(PolicyReason.InvalidCaller, Evaluator().Evaluate(Request, new SyntheticCaller(AuthenticationContextId: "")).Reason);
        Assert.Equal(PolicyReason.InvalidRequest, Evaluator().Revalidate(Request, new SyntheticCaller(), null!).Reason);
    }

    [Fact]
    public void EvaluationErrorsUseFixedReasonAndNoExceptionText()
    {
        var results = new[] {
            new DeterministicPolicyEvaluator(new BrokenSource(), new FakeClock()).Evaluate(Request, new SyntheticCaller()),
            Evaluator().Evaluate(Request, new BrokenCaller()),
            Evaluator(clock: new BrokenClock()).Evaluate(Request, new SyntheticCaller()) };
        Assert.All(results, result => { Assert.Equal(PolicyOutcome.Deny, result.Outcome); Assert.Equal(PolicyReason.EvaluationError, result.Reason); });
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(results));
    }

    [Fact]
    public void CurrentDenyOverridesOldAllowAndFabricatedApproval()
    {
        var old = Evaluator().Evaluate(Request, new SyntheticCaller());
        var deny = Evaluator(Snapshot(Rule(PolicyOutcome.Deny)));
        Assert.Equal(PolicyReason.ExplicitDeny, deny.Revalidate(Request, new SyntheticCaller(), old).Reason);
        var required = Evaluator(Snapshot(Rule(PolicyOutcome.RequireApproval)));
        Assert.Equal(PolicyOutcome.RequireApproval, required.Revalidate(Request, new SyntheticCaller(), old).Outcome);
        Assert.Equal(PolicyOutcome.Deny, Evaluator().Revalidate(Request, new SyntheticCaller(), old with { Outcome = PolicyOutcome.RequireApproval }).Outcome);
    }

    [Theory]
    [InlineData(0, PolicyOutcome.Allow)]
    [InlineData(299, PolicyOutcome.Allow)]
    [InlineData(300, PolicyOutcome.Deny)]
    [InlineData(301, PolicyOutcome.Deny)]
    [InlineData(-1, PolicyOutcome.Deny)]
    public void ExpirationAndClockRollbackFailClosed(int seconds, PolicyOutcome expected)
    {
        var clock = new FakeClock(); var evaluator = Evaluator(clock: clock);
        var old = evaluator.Evaluate(Request, new SyntheticCaller()); clock.Now = Now.AddSeconds(seconds);
        var result = evaluator.Revalidate(Request, new SyntheticCaller(), old);
        Assert.Equal(expected, result.Outcome);
        if (expected == PolicyOutcome.Allow) Assert.Equal(old.NotAfter, result.NotAfter);
        else Assert.Equal(seconds < 0 ? PolicyReason.DecisionNotYetValid : PolicyReason.DecisionExpired, result.Reason);
    }

    [Fact]
    public void RevalidationBindsNewAuthorityEpochVersionAndOperation()
    {
        var old = Evaluator().Evaluate(Request, new SyntheticCaller());
        var next = Snapshot(Rule()) with { Epoch = new(2) };
        Assert.Equal(PolicyReason.PolicyEpochMismatch, Evaluator(next).Revalidate(Request with { Epoch = new(2) }, new SyntheticCaller(), old).Reason);
        next = Snapshot(Rule()) with { Version = new(2) };
        Assert.Equal(PolicyReason.PolicyVersionMismatch, Evaluator(next).Revalidate(Request with { Version = new(2) }, new SyntheticCaller(), old).Reason);
        next = Snapshot(Rule()) with { AuthorityId = "new-authority" };
        Assert.Equal(PolicyReason.AuthorityMismatch, Evaluator(next).Revalidate(Request with { AuthorityId = "new-authority" }, new SyntheticCaller(), old).Reason);
        Assert.Equal(PolicyReason.BindingMismatch, Evaluator().Revalidate(Request with { TaskId = "different-task" }, new SyntheticCaller(), old).Reason);
    }

    [Fact]
    public void SnapshotExpiryClampsDecisionAndReevaluationDoesNotRenewIt()
    {
        var snapshot = Snapshot(Rule()) with { NotAfter = Now.AddSeconds(10) };
        Assert.Equal(snapshot.NotAfter, Evaluator(snapshot).Evaluate(Request, new SyntheticCaller()).NotAfter);
    }

    [Fact]
    public void ConflictingApprovalScopesAndUnavailableDescriptorsFailClosed()
    {
        var conflicting = Snapshot(Rule(PolicyOutcome.RequireApproval), Rule(PolicyOutcome.RequireApproval) with { RequiredOperatorScope = "different" });
        Assert.Equal(PolicyReason.InvalidPolicy, Evaluator(conflicting).Evaluate(Request, new SyntheticCaller()).Reason);
        var unsupported = Snapshot(Rule()) with { Capabilities = [Descriptor with { Boundary = EnforcementBoundary.ProviderChallenge }] };
        Assert.Equal(PolicyReason.NotSupported, Evaluator(unsupported).Evaluate(Request, new SyntheticCaller()).Reason);
        Assert.Equal(PolicyReason.UnknownCapability, Evaluator(Snapshot(Rule()) with { Capabilities = [] }).Evaluate(Request, new SyntheticCaller()).Reason);
    }

    [Fact]
    public void RevalidationDoesNotAuthorizeAnotherScopeEvenIfNewScopeIsAllowed()
    {
        var nextScope = Scope with { ResourceId = "resource:another" };
        var evaluator = Evaluator(Snapshot(Rule(), Rule() with { Scope = nextScope }));
        var old = evaluator.Evaluate(Request, new SyntheticCaller());
        Assert.Equal(PolicyReason.BindingMismatch, evaluator.Revalidate(Request with { Scope = nextScope }, new SyntheticCaller(), old).Reason);
        Assert.Equal(PolicyReason.ScopeMismatch, Evaluator().Revalidate(Request with { Scope = nextScope }, new SyntheticCaller(), old).Reason);
    }
}
