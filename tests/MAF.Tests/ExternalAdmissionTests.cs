using System.Collections.Immutable;
using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Workflows.External;

namespace MAF.Tests;

public sealed class ExternalAdmissionTests
{
    static readonly DateTimeOffset Now = new(2026, 10, 16, 0, 0, 0, TimeSpan.Zero);
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(30);
    static readonly string Root = DurableExecution.NormalizeRoot(Path.Combine(Path.GetPathRoot(Path.GetFullPath("."))!, "maf-synthetic-admission"));
    static readonly CapabilityDescriptor Submit = new("connector", "adapter-1", PolicyCapability.ReadOnlyTask,
        EnforcementBoundary.TaskAdmission, [PolicyOperation.SubmitNewReadOnlyTask], true, true);
    static readonly CapabilityDescriptor Inspect = Submit with
    { Capability = PolicyCapability.ExecutionInspection, SupportedOperations = [PolicyOperation.InspectExistingExecution] };
    static readonly AuthorizationScope Scope = new(Root, new('A', 64), new('B', 64), 7, 2, ResourceAccess.ReadOnly, Budget.Ticks);
    static ExternalTaskRequest TaskRequest => new("task", Root, "Synthetic read-only question", Budget);
    static ExternalAdmissionRequest Request => new(ExternalAdmissionMode.Submit, "task", Budget, TaskRequest);
    static ExternalAdmissionInputs Inputs => new("authority", new(3), new(4), "Synthetic", Scope with { BudgetTicks = TimeSpan.FromMinutes(1).Ticks },
        Submit, Inspect, "operation", "new-run", new("new-execution", "Synthetic", ExternalRequestId: "new-message"), []);
    static PolicySnapshot Policy(PolicyOutcome outcome = PolicyOutcome.Allow) => new("authority", new(3), new(4),
        Now.AddHours(-1), Now.AddHours(1), TimeSpan.FromMinutes(5), [Submit, Inspect],
        [Rule(Submit, PolicyOperation.SubmitNewReadOnlyTask, outcome), Rule(Inspect, PolicyOperation.InspectExistingExecution, outcome)]);
    static PolicyRule Rule(CapabilityDescriptor descriptor, PolicyOperation operation, PolicyOutcome outcome)
        => new("agent", descriptor.ConnectorId, descriptor.Capability, operation, Scope, outcome,
            outcome == PolicyOutcome.RequireApproval ? "operator:readonly" : null);
    static DurableExecution Existing => new("task", DurableExecution.Fingerprint(TaskRequest), Scope.ProviderScopeDigest, Root,
        "original-run", new("original-execution", "Synthetic", "ses_original", "msg_original"),
        DurableExecutionState.Running, SubmissionPhase.Acknowledged, Now.AddMinutes(-1), Now);
    sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
    sealed class BrokenClock : TimeProvider { public override DateTimeOffset GetUtcNow() => throw new IOException("private clock detail"); }
    sealed record Caller(string SubjectId = "agent", string AuthenticationContextId = "test-transport") : ITrustedCallerContext;

    // This dependency exposes read-only policy/auth through the interfaces given to the component.
    // Every execution/storage/Workflow/activation callback is an armed trap on the same object.
    // Merely holding an Allow assessment must never reach any of them. No real DB/provider exists.
    sealed class Boundary : IPolicySource, ITrustedIdentityResolver, IExternalHarnessClient, IExecutionStore, IPolicyActivationCoordinator
    {
        public readonly Caller Context = new();
        public PolicySnapshot? Snapshot = Policy();
        public AuthenticatedIdentity? Identity = new("agent", "test-transport", IdentityKind.Agent, Now.AddMinutes(-1), Now.AddMinutes(1), false);
        public bool SourceThrows, ResolverThrows;
        public int PolicyReads;
        readonly Dictionary<string, int> effects = new(StringComparer.Ordinal)
        {
            ["Validate"] = 0, ["Create"] = 0, ["Submit"] = 0, ["Observe"] = 0, ["Abort"] = 0,
            ["AcquireOwner"] = 0, ["ReadDatabase"] = 0, ["ListDatabase"] = 0, ["InsertDatabase"] = 0,
            ["UpdateDatabase"] = 0, ["Workflow"] = 0, ["PersistApproval"] = 0, ["Activate"] = 0
        };
        T Trap<T>(string effect) { effects[effect]++; throw new InvalidOperationException("Unexpected effect: " + effect); }
        public void AssertNoEffects() => Assert.All(effects, entry => Assert.Equal(0, entry.Value));
        public PolicySnapshot? GetSnapshot() { PolicyReads++; return SourceThrows ? throw new IOException("private policy detail") : Snapshot; }
        public AuthenticatedIdentity? Resolve(ITrustedCallerContext context)
        {
            if (ResolverThrows) throw new IOException("private identity detail");
            return ReferenceEquals(context, Context) ? Identity : null;
        }
        public string Provider => "Synthetic";
        public int GetRequestCount(string taskId) => 0;
        public Task ValidateAsync(ExternalTaskRequest request, CancellationToken token) => Trap<Task>("Validate");
        public Task<ExternalExecutionHandle> CreateAsync(ExternalTaskRequest request, string run, ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<ExternalExecutionHandle>>("Create");
        public Task SubmitAsync(ExternalTaskRequest request, ExternalExecutionHandle handle, CancellationToken token) => Trap<Task>("Submit");
        public Task<ExternalObservation> ObserveAsync(ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<ExternalObservation>>("Observe");
        public Task<bool> AbortAsync(ExternalExecutionHandle handle, CancellationToken token) => Trap<Task<bool>>("Abort");
        public ValueTask<IExecutionOwnership?> TryAcquireAsync(string task, CancellationToken token = default) => Trap<ValueTask<IExecutionOwnership?>>("AcquireOwner");
        public Task<DurableExecution?> GetAsync(string task, CancellationToken token = default) => Trap<Task<DurableExecution?>>("ReadDatabase");
        public Task<IReadOnlyList<DurableExecution>> ListRecoverableAsync(CancellationToken token = default) => Trap<Task<IReadOnlyList<DurableExecution>>>("ListDatabase");
        public Task CreateAsync(DurableExecution execution, IExecutionOwnership owner, CancellationToken token = default) => Trap<Task>("InsertDatabase");
        public Task UpdateAsync(DurableExecution expected, DurableExecution next, IExecutionOwnership owner, CancellationToken token = default) => Trap<Task>("UpdateDatabase");
        public Task ActivatePolicyAsync(CancellationToken token = default) => Trap<Task>("Activate");
        public Task RunWorkflow() => Trap<Task>("Workflow");
        public Task PersistApproval() => Trap<Task>("PersistApproval");
    }
    static ExternalAdmissionResult Assess(Boundary boundary, ExternalAdmissionInputs? inputs = null,
        ExternalAdmissionRequest? request = null, ITrustedCallerContext? caller = null, bool missingCaller = false)
    {
        try
        {
            return new ExternalExecutionApplication(inputs ?? Inputs, boundary, boundary, new Clock())
                .Assess(request ?? Request, missingCaller ? null : caller ?? boundary.Context);
        }
        finally { boundary.AssertNoEffects(); }
    }
    static void Denied(ExternalAdmissionResult result, PolicyReason reason)
    {
        Assert.Equal(PolicyOutcome.Deny, result.Decision.Outcome); Assert.Equal(reason, result.Decision.Reason);
        Assert.Null(result.Decision.BindingDigest); Assert.Null(result.Operation);
    }

    [Theory]
    [InlineData(PolicyOutcome.Allow, PolicyReason.Allowed)]
    [InlineData(PolicyOutcome.Deny, PolicyReason.ExplicitDeny)]
    [InlineData(PolicyOutcome.RequireApproval, PolicyReason.ApprovalRequired)]
    public void ThreeOutcomesAreNonExecutable(PolicyOutcome outcome, PolicyReason reason)
    {
        var b = new Boundary { Snapshot = Policy(outcome) }; var result = Assess(b);
        Assert.Equal(outcome, result.Decision.Outcome); Assert.Equal(reason, result.Decision.Reason);
        Assert.Equal(ExternalAdmissionClassification.NewSubmission, result.Classification);
        Assert.Equal(1, b.PolicyReads);
        if (outcome == PolicyOutcome.Deny) { Denied(result, reason); return; }
        var op = Assert.IsType<PolicyRequest>(result.Operation);
        Assert.Equal("agent", op.CallerSubject); Assert.Equal("authority", op.AuthorityId);
        Assert.Equal(Scope, op.Scope); Assert.Equal(DurableExecution.Fingerprint(TaskRequest), op.RequestFingerprint);
        Assert.Equal(Inputs.NewWorkflowRunId, op.WorkflowRunId); Assert.Null(op.ProviderSessionId);
        if (outcome != PolicyOutcome.Deny) Assert.Equal(OperationBinding.Digest(op, Submit), result.Decision.BindingDigest);
        Assert.Equal(outcome == PolicyOutcome.RequireApproval ? "operator:readonly" : null, result.Decision.RequiredOperatorScope);
        Assert.Equal(b.Identity!.NotAfter, result.Decision.NotAfter);
    }
    [Theory]
    [InlineData(ExternalAdmissionMode.Submit)] [InlineData(ExternalAdmissionMode.EphemeralSubmit)] [InlineData(ExternalAdmissionMode.Inspect)]
    public void ExistingTaskAlwaysBecomesInspectionWithOriginalCorrelation(ExternalAdmissionMode mode)
    {
        var b = new Boundary(); var request = Request with { Mode = mode, Submission = mode == ExternalAdmissionMode.Inspect ? null : TaskRequest };
        var result = Assess(b, Inputs with { Executions = [Existing] }, request);
        Assert.Equal(ExternalAdmissionClassification.ExistingInspection, result.Classification);
        Assert.Equal(PolicyOutcome.Allow, result.Decision.Outcome);
        var op = result.Operation!; Assert.Equal(PolicyOperation.InspectExistingExecution, op.Operation);
        Assert.Equal(Existing.WorkflowRunId, op.WorkflowRunId); Assert.Equal(Existing.Handle.ExecutionId, op.ExecutionId);
        Assert.Equal(Existing.Handle.ExternalSessionId, op.ProviderSessionId); Assert.Equal(Existing.Handle.ExternalRequestId, op.MessageId);
        Assert.Equal(Existing.RequestFingerprint, op.RequestFingerprint);
        Assert.Equal(OperationBinding.Digest(op, Inspect), result.Decision.BindingDigest);
    }
    [Fact]
    public void EphemeralIntentIsDistinctWithoutBecomingAnExecutionFallback()
    {
        var b = new Boundary(); var result = Assess(b, request: Request with { Mode = ExternalAdmissionMode.EphemeralSubmit });
        Assert.Equal(ExternalAdmissionClassification.EphemeralSubmission, result.Classification);
        Assert.Equal(PolicyOutcome.Allow, result.Decision.Outcome);
        b.Snapshot = Policy(PolicyOutcome.RequireApproval);
        Assert.Equal(PolicyOutcome.RequireApproval, Assess(b, request: Request with { Mode = ExternalAdmissionMode.EphemeralSubmit }).Decision.Outcome);
    }
    [Fact]
    public void BatchIsClassifiedButDoesNotInferGlobalListPermission()
    {
        var b = new Boundary(); var result = Assess(b, Inputs with { Executions = [Existing] }, Request with { Mode = ExternalAdmissionMode.BatchInspect });
        Assert.Equal(ExternalAdmissionClassification.BatchInspection, result.Classification);
        Denied(result, PolicyReason.NotSupported); Assert.Null(result.Operation); Assert.Equal(0, b.PolicyReads);
    }
    [Theory]
    [InlineData(ExternalAdmissionMode.Unknown, PolicyReason.UnknownOperation)]
    [InlineData((ExternalAdmissionMode)900, PolicyReason.UnknownOperation)]
    [InlineData(ExternalAdmissionMode.Cancel, PolicyReason.NotSupported)]
    public void UnknownOrUnsupportedOperationsFailClosed(ExternalAdmissionMode mode, PolicyReason reason)
        => Denied(Assess(new Boundary(), request: Request with { Mode = mode }), reason);

    [Theory]
    [InlineData("missing")][InlineData("forged")][InlineData("unavailable")][InlineData("subject")]
    [InlineData("context")][InlineData("expired")][InlineData("future")][InlineData("unknown-kind")]
    public void CallerMustBeVerifiedByControlledResolver(string change)
    {
        var b = new Boundary(); ITrustedCallerContext? caller = null;
        switch (change)
        {
            case "forged": caller = new Caller(); break; // identical claims, not a verified transport context
            case "unavailable": b.Identity = null; break;
            case "subject": b.Identity = b.Identity! with { SubjectId = "other" }; break;
            case "context": b.Identity = b.Identity! with { AuthenticationContextId = "other" }; break;
            case "expired": b.Identity = b.Identity! with { NotAfter = Now }; break;
            case "future": b.Identity = b.Identity! with { NotBefore = Now.AddTicks(1) }; break;
            case "unknown-kind": b.Identity = b.Identity! with { Kind = (IdentityKind)900 }; break;
        }
        Denied(Assess(b, caller: caller, missingCaller: change == "missing"), PolicyReason.InvalidCaller);
        Assert.Equal(0, b.PolicyReads);
    }
    [Theory]
    [InlineData("missing", PolicyReason.PolicyUnavailable)] [InlineData("expired", PolicyReason.InvalidPolicy)]
    [InlineData("invalid", PolicyReason.InvalidPolicy)] [InlineData("authority", PolicyReason.AuthorityMismatch)]
    [InlineData("epoch", PolicyReason.PolicyEpochMismatch)] [InlineData("version", PolicyReason.PolicyVersionMismatch)]
    [InlineData("source-error", PolicyReason.EvaluationError)] [InlineData("identity-error", PolicyReason.EvaluationError)]
    public void PolicyFailuresNeverProduceAllow(string change, PolicyReason reason)
    {
        var b = new Boundary();
        switch (change)
        {
            case "missing": b.Snapshot = null; break;
            case "expired": b.Snapshot = b.Snapshot! with { NotAfter = Now }; break;
            case "invalid": b.Snapshot = b.Snapshot! with { DecisionLifetime = TimeSpan.Zero }; break;
            case "authority": b.Snapshot = b.Snapshot! with { AuthorityId = "other" }; break;
            case "epoch": b.Snapshot = b.Snapshot! with { Epoch = new(4) }; break;
            case "version": b.Snapshot = b.Snapshot! with { Version = new(5) }; break;
            case "source-error": b.SourceThrows = true; break;
            case "identity-error": b.ResolverThrows = true; break;
        }
        Denied(Assess(b), reason);
    }
    [Theory]
    [InlineData("scope", PolicyReason.ScopeMismatch)] [InlineData("revision", PolicyReason.ScopeMismatch)]
    [InlineData("capability", PolicyReason.UnknownCapability)] [InlineData("unknown-capability", PolicyReason.UnknownCapability)]
    [InlineData("connector", PolicyReason.UnknownCapability)] [InlineData("adapter", PolicyReason.BindingMismatch)]
    [InlineData("descriptor-state", PolicyReason.BindingMismatch)] [InlineData("provider", PolicyReason.BindingMismatch)]
    [InlineData("invalid-epoch", PolicyReason.InvalidRequest)] [InlineData("invalid-version", PolicyReason.InvalidRequest)]
    [InlineData("invalid-hash", PolicyReason.InvalidRequest)] [InlineData("invalid-root", PolicyReason.ScopeMismatch)]
    public void TrustedApplicationInputsMustMatchPolicyAndBinding(string change, PolicyReason reason)
    {
        var input = Inputs;
        input = change switch
        {
            "scope" => input with { Scope = input.Scope with { AgentPolicyDigest = new('C', 64) } },
            "revision" => input with { Scope = input.Scope with { ConfigurationRevision = 8 } },
            "capability" => input with { SubmissionCapability = Submit with { Capability = PolicyCapability.Unknown } },
            "unknown-capability" => input with { SubmissionCapability = Submit with { Capability = (PolicyCapability)900 } },
            "connector" => input with { SubmissionCapability = Submit with { ConnectorId = "other" } },
            "adapter" => input with { SubmissionCapability = Submit with { AdapterVersion = "other" } },
            "descriptor-state" => input with { SubmissionCapability = Submit with { Healthy = false } },
            "provider" => input with { ProviderId = "other" },
            "invalid-epoch" => input with { Epoch = new(0) },
            "invalid-version" => input with { Version = new(0) },
            "invalid-hash" => input with { Scope = input.Scope with { ProviderScopeDigest = "bad" } },
            _ => input with { Scope = input.Scope with { ResourceId = "relative-root" } }
        };
        Denied(Assess(new Boundary(), input), reason);
    }
    [Theory]
    [InlineData("root", PolicyReason.ScopeMismatch)] [InlineData("changed-input", PolicyReason.BindingMismatch)]
    [InlineData("existing-scope", PolicyReason.ScopeMismatch)] [InlineData("existing-provider", PolicyReason.BindingMismatch)]
    [InlineData("missing-task", PolicyReason.InvalidRequest)] [InlineData("new-session", PolicyReason.BindingMismatch)]
    [InlineData("write", PolicyReason.NotSupported)] [InlineData("budget", PolicyReason.ScopeMismatch)]
    public void SubmissionAndExistingEvidenceCannotBeRebound(string change, PolicyReason reason)
    {
        var input = Inputs; var request = Request;
        switch (change)
        {
            case "root": request = request with { Submission = TaskRequest with { ProjectRoot = Path.Combine(Root, "other") } }; break;
            case "changed-input": input = input with { Executions = [Existing] }; request = request with { Submission = TaskRequest with { TaskText = "different" } }; break;
            case "existing-scope": input = input with { Executions = [Existing with { ProviderScope = new('D', 64) }] }; break;
            case "existing-provider": input = input with { Executions = [Existing with { Handle = Existing.Handle with { Provider = "other" } }] }; break;
            case "missing-task": request = request with { Mode = ExternalAdmissionMode.Inspect, Submission = null }; break;
            case "new-session": input = input with { PreparedHandle = input.PreparedHandle with { ExternalSessionId = "ses_claimed" } }; break;
            case "write": request = request with { Submission = TaskRequest with { Policy = (ExternalExecutionPolicy)900 } }; break;
            case "budget": input = input with { Scope = Scope with { BudgetTicks = Budget.Ticks - 1 } }; break;
        }
        Denied(Assess(new Boundary(), input, request), reason);
    }
    [Theory]
    [InlineData("authorityId")][InlineData("epoch")][InlineData("scope")][InlineData("capability")][InlineData("callerSubject")]
    public void IngressCannotSupplyTrustClaims(string property)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(Request))!;
        node[property] = "attacker-claim";
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<ExternalAdmissionRequest>(node.ToJsonString()));
        Assert.DoesNotContain(typeof(ExternalAdmissionRequest).GetProperties(), p => p.Name.Equals(property, StringComparison.OrdinalIgnoreCase));
    }
    [Fact]
    public void MissingCompositionAndClockErrorsFailClosed()
    {
        var b = new Boundary();
        Denied(new ExternalExecutionApplication(Inputs, b, null, new Clock()).Assess(Request, b.Context), PolicyReason.InvalidCaller);
        Denied(new ExternalExecutionApplication(Inputs, null, b, new Clock()).Assess(Request, b.Context), PolicyReason.PolicyUnavailable);
        Denied(new ExternalExecutionApplication(Inputs, b, b, new BrokenClock()).Assess(Request, b.Context), PolicyReason.EvaluationError);
        b.AssertNoEffects();
    }
    [Fact]
    public void AssessmentIsDeterministicAndHasNoExecutionDependencies()
    {
        var b = new Boundary(); Assert.Equal(Assess(b), Assess(b));
        var constructor = Assert.Single(typeof(ExternalExecutionApplication).GetConstructors());
        Assert.DoesNotContain(constructor.GetParameters(), p => p.ParameterType == typeof(IExternalHarnessClient)
            || p.ParameterType == typeof(IExecutionStore) || p.ParameterType == typeof(ExternalWorkflow)
            || typeof(Delegate).IsAssignableFrom(p.ParameterType));
        Assert.DoesNotContain(typeof(ExternalAdmissionResult).GetProperties(), p => typeof(Delegate).IsAssignableFrom(p.PropertyType));
    }
    [Fact]
    public void ExistingInspectionUsesIndependentPolicyAndDeniedAssessmentHidesCorrelation()
    {
        var b = new Boundary(); var input = Inputs with { Executions = [Existing] };
        b.Snapshot = Policy() with { Rules = [Rule(Submit, PolicyOperation.SubmitNewReadOnlyTask, PolicyOutcome.Deny),
            Rule(Inspect, PolicyOperation.InspectExistingExecution, PolicyOutcome.Allow)] };
        Assert.Equal(PolicyOutcome.Allow, Assess(b, input).Decision.Outcome);
        b.Snapshot = Policy() with { Rules = [Rule(Submit, PolicyOperation.SubmitNewReadOnlyTask, PolicyOutcome.Allow),
            Rule(Inspect, PolicyOperation.InspectExistingExecution, PolicyOutcome.Deny)] };
        var denied = Assess(b, input); Denied(denied, PolicyReason.ExplicitDeny);
        Assert.DoesNotContain("ses_original", JsonSerializer.Serialize(denied));
        Assert.DoesNotContain("original-run", JsonSerializer.Serialize(denied));
    }
    [Fact]
    public void DecisionDeadlineCannotOutliveEitherPolicyOrVerifiedIdentity()
    {
        var b = new Boundary(); b.Identity = b.Identity! with { NotAfter = Now.AddSeconds(10) };
        Assert.Equal(Now.AddSeconds(10), Assess(b).Decision.NotAfter);
        b.Snapshot = b.Snapshot! with { NotAfter = Now.AddSeconds(5) };
        Assert.Equal(Now.AddSeconds(5), Assess(b).Decision.NotAfter);
    }
    [Theory]
    [InlineData("task-id")][InlineData("timeout")][InlineData("missing-submission")][InlineData("text")]
    [InlineData("inspection-input")][InlineData("duplicate-snapshot")]
    public void MalformedIntentOrAmbiguousSnapshotFailsClosed(string change)
    {
        var input = Inputs; var request = Request; var reason = PolicyReason.InvalidRequest;
        switch (change)
        {
            case "task-id": request = request with { TaskId = "other" }; break;
            case "timeout": request = request with { Timeout = TimeSpan.Zero }; break;
            case "missing-submission": request = request with { Submission = null }; break;
            case "text": request = request with { Submission = TaskRequest with { TaskText = "" } }; break;
            case "inspection-input": request = request with { Mode = ExternalAdmissionMode.Inspect }; break;
            case "duplicate-snapshot": input = input with { Executions = [Existing, Existing] }; reason = PolicyReason.EvaluationError; break;
        }
        Denied(Assess(new Boundary(), input, request), reason);
    }
}
