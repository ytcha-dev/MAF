using System.Collections.Immutable;
using System.Text.Json;
using MAF.Agents.Review;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Core.Tools;
using MAF.Host;
using MAF.Workflows.External;
using Microsoft.Agents.AI.Workflows;
using Xunit.Abstractions;

namespace MAF.Tests;

// Test-owned composition only. Reuses the 4E provider fixture and actual R1 native Agent.
public sealed class LocalSoloWorkflowTests(ITestOutputHelper output) : IDisposable
{
    readonly SourceCandidateIntegrationTests fixtures = new(output);
    static string Present(LocalResultView view, bool diagnostics = false)
    {
        using var text = new StringWriter(); LocalSoloPresentation.Result(text, view, diagnostics); return text.ToString();
    }
    static int[] Counts(SyntheticProtectedHandoff f) => [f.Fake.Requests.Count(x => x == "POST /session"),
        f.Fake.Requests.Count(x => x.EndsWith("/prompt_async")), f.Fake.Requests.Count(x => x.StartsWith("GET ")),
        f.Fake.Requests.Count(x => x.EndsWith("/abort"))];
    static void Graph(HandoffRun run)
    {
        Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" },
            run.Events.OfType<ExecutorInvokedEvent>().Select(e => e.ExecutorId));
        Assert.Single(run.Events.OfType<WorkflowOutputEvent>());
    }
    static void NonExecutable(LocalResultView view)
    {
        Assert.Equal(LocalViewKind.ControlledOfflineFixture, view.Kind);
        Assert.Equal(LocalHumanChoice.NotEstablished, view.HumanChoice);
        Assert.Contains("ControlledOfflineFixture / NotLiveProvider", Present(view));
        Assert.DoesNotContain("Human result choice: Accepted", Present(view));
    }
    [Fact]
    public async Task OfficialProtectedJourneyMapsActualNativeEvidenceWithoutOperationalActivation()
    {
        using var f = await fixtures.Fixture(); using var model = new SourceCandidateIntegrationTests.Reviewer();
        var reader = new BoundedSourceReader(f.F.Runtime.Root, f.F.Clock);
        var flow = f.Flow(sources: new VerifiedSourceMode(reader), reviewer: new NativeEvidenceReviewer(model).ReviewAsync);
        var task = f.Task;
        var run = await flow.RunAsync(task); Graph(run);
        var view = LocalSoloOutcomeMapper.FromControlledWorkflow(run.Outcome); NonExecutable(view);
        Assert.Equal(task, f.Task); Assert.Equal(task.TaskId, view.TaskId); Assert.Equal(task.WorkflowRunId, view.WorkflowRunId);
        var row = (await f.F.Executions.GetAsync(task.TaskId))!;
        Assert.Equal(row.Handle, run.Outcome.ExternalResult!.Handle);
        Assert.Equal(row.Handle.ExecutionId, view.ExecutionId); Assert.Equal(row.Handle.Provider, view.Harness);
        Assert.Equal("ses_runtime", row.Handle.ExternalSessionId); Assert.Equal("msg_runtime", row.Handle.ExternalRequestId);
        Assert.Equal(SessionReceiptStatus.Verified, (await f.F.Store().GetSessionReceiptAsync(task.TaskId))!.Status);
        Assert.Equal(ExternalExecutionStatus.Completed, view.Status);
        Assert.Equal(EvidenceAssessmentStatus.SufficientEvidence, view.ReviewStatus);
        Assert.Equal(HumanGateStatus.ReadyForHumanReview, view.Readiness);
        Assert.Single(view.Evidence); Assert.Contains("independently acquired review-time", view.Evidence[0].Provenance);
        Assert.Contains("Example.cs:120", model.ToolEvidence); Assert.Equal(2, model.Calls); Assert.Equal(2, reader.ReadAttempts);
        Assert.Equal(1, f.PcaCalls); Assert.Equal(0, f.ReviewCalls);
        Assert.Equal("NotEstablished", view.MafApproval); Assert.Equal("NotEstablished", view.HarnessPermission);
        Assert.DoesNotContain("ses_runtime", Present(view)); Assert.DoesNotContain("msg_runtime", Present(view));
        Assert.Contains("ses_runtime", Present(view, true)); Assert.Contains("msg_runtime", Present(view, true));
        Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f));
        var again = await flow.RunAsync(task); Assert.Same(run, again); Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f));
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.RunAsync(task with { Objective = "changed" }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.RunAsync(task with { Workspace = task.Workspace + "-outside" }));
        Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f));
        output.WriteLine($"Task={view.TaskId}; Run={view.WorkflowRunId}; Execution={view.ExecutionId}; Session={row.Handle.ExternalSessionId}; Request={row.Handle.ExternalRequestId}; Create/Prompt/GET/Abort=1/1/9/0; SDK model calls={model.Calls}");
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task OuterAllowStillCannotOverrideActualPolicyDenialOrApproval(bool approval)
    {
        using var f = await fixtures.Fixture(); using var model = new SourceCandidateIntegrationTests.Reviewer();
        f.F.Policy = f.F.Policy with { Epoch = new(2), Version = new(2), Rules = [..f.F.Policy.Rules.Select(r =>
            r.Operation == PolicyOperation.SubmitNewReadOnlyTask ? r with { Outcome = approval ? PolicyOutcome.RequireApproval : PolicyOutcome.Deny,
                RequiredOperatorScope = approval ? "operator:readonly" : null } : r)] };
        await f.F.Store().ActivatePolicyAsync();
        var store = f.F.Store(); var runtime = new ProtectedExternalRuntime(f.F.Runtime.Inputs with { Epoch = new(2), Version = new(2) },
            new ImmutablePolicySource(f.F.Policy), f.F, f.F.Executions, store, store, store, f.Http, f.F.Runtime.Options, f.F.Clock);
        var run = await f.Flow(runtime, sources: new VerifiedSourceMode(new(f.F.Runtime.Root, f.F.Clock)),
            reviewer: new NativeEvidenceReviewer(model).ReviewAsync).RunAsync(f.Task); Graph(run);
        var view = LocalSoloOutcomeMapper.FromControlledWorkflow(run.Outcome); NonExecutable(view);
        Assert.Null(view.Status); Assert.Null(view.ExecutionId); Assert.Null(view.ReviewStatus);
        Assert.Contains("ExternalDelegation: Skipped", Present(view)); Assert.Contains("Execution: Unavailable", Present(view));
        Assert.Empty(f.Fake.Requests); Assert.Equal(0, model.Calls); Assert.Null(await f.F.Executions.GetAsync(f.Task.TaskId));
        Assert.Equal(approval ? HumanGateStatus.WaitingForApproval : HumanGateStatus.Blocked, view.Readiness);
        Assert.Equal(approval ? "RequireApproval (not approved or executable)" : "NotEstablished", view.MafApproval);
        Assert.Equal("NotEstablished", view.HarnessPermission);
    }
    [Theory]
    [InlineData("missing")] [InlineData("changed")] [InlineData("partial-observation")] [InlineData("reviewer-unavailable")]
    public async Task CompletedDoesNotImplySuccessfulReview(string variant)
    {
        using var timing = new OfflineCheckpointTiming(output.WriteLine);
        using var f = await fixtures.Fixture(variant: variant == "missing" ? "none" : "valid", sources: variant == "partial-observation" ? 2 : 1,
            operationTimeout: TimeSpan.FromSeconds(5));
        using var model = new SourceCandidateIntegrationTests.Reviewer { Partial = variant == "partial-observation" };
        var native = new NativeEvidenceReviewer(model);
        Func<TaskEvidence, CancellationToken, Task<ReviewAssessment>> review = variant == "reviewer-unavailable"
            ? (_, _) => throw new InvalidOperationException("SyntheticReviewerUnavailable") : native.ReviewAsync;
        var run = await f.Flow(sources: new VerifiedSourceMode(new(f.F.Runtime.Root, f.F.Clock)), reviewer: review,
            handoff: variant == "changed" ? () => { var path = Path.Combine(f.F.Runtime.Root, "Example.cs"); File.WriteAllText(path, File.ReadAllText(path).Replace("return 42;", "return 77;")); } : null).RunAsync(f.Task);
        timing.Observe(run.Outcome);
        Graph(run); var view = LocalSoloOutcomeMapper.FromControlledWorkflow(run.Outcome); NonExecutable(view);
        if (view.Status != ExternalExecutionStatus.Completed)
            output.WriteLine($"Completion prerequisite unavailable: external={view.Status}; "
                + $"code={run.Outcome.ExternalResult?.ErrorCode}; elapsedMs={run.Outcome.ExternalResult?.DurationMilliseconds}; "
                + $"reason={run.Outcome.ReasonCode}; trace={string.Join(',', run.Outcome.ExternalResult?.Trace.Select(t => t.Kind) ?? [])}");
        Assert.Equal(ExternalExecutionStatus.Completed, view.Status);
        Assert.Equal(variant == "reviewer-unavailable" ? EvidenceAssessmentStatus.ReviewUnavailable : EvidenceAssessmentStatus.InsufficientEvidence, view.ReviewStatus);
        Assert.Equal(HumanGateStatus.NeedsHumanReview, view.Readiness); Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f));
        Assert.Contains(view.ReviewStatus.ToString()!, Present(view)); Assert.NotEmpty(view.Limitations);
        if (variant is "missing" or "changed") { Assert.Empty(view.Evidence); Assert.Equal(0, model.Calls); }
    }
    [Fact]
    public async Task LostCreateRetainsUnknownAndOriginalIdentityWithoutReplacementOrRetry()
    {
        using var f = await fixtures.Fixture(); f.Fake.LoseCreateResponse = true;
        using var model = new SourceCandidateIntegrationTests.Reviewer();
        var flow = f.Flow(sources: new VerifiedSourceMode(new(f.F.Runtime.Root, f.F.Clock)), reviewer: new NativeEvidenceReviewer(model).ReviewAsync);
        var run = await flow.RunAsync(f.Task); Graph(run);
        var view = LocalSoloOutcomeMapper.FromControlledWorkflow(run.Outcome); NonExecutable(view);
        Assert.Equal(ExternalExecutionStatus.Unknown, view.Status); Assert.Equal("runtime-execution", view.ExecutionId);
        Assert.Null(run.Outcome.ExternalResult!.Handle.ExternalSessionId);
        Assert.Equal(1, Counts(f)[0]); Assert.Equal(0, Counts(f)[1]); Assert.Equal(0, Counts(f)[3]);
        Assert.Null(view.ReviewStatus); Assert.Equal(0, model.Calls);
        await flow.RunAsync(f.Task); Assert.Equal(1, Counts(f)[0]); Assert.Equal(0, Counts(f)[1]);
        Assert.Contains("Unknown", Present(view)); Assert.Equal(HumanGateStatus.NeedsHumanReview, view.Readiness);
    }
    [Fact]
    public async Task HarnessPermissionObservationNeverBecomesMafApprovalOrHumanChoice()
    {
        using var f = await fixtures.Fixture(); f.Fake.Pending = true;
        var run = await f.Flow(sources: new VerifiedSourceMode(new(f.F.Runtime.Root, f.F.Clock))).RunAsync(f.Task);
        var view = LocalSoloOutcomeMapper.FromControlledWorkflow(run.Outcome); NonExecutable(view);
        Assert.Equal(ExternalExecutionStatus.WaitingForApproval, view.Status);
        Assert.Equal("NotEstablished", view.MafApproval); Assert.Contains("authority NotEstablished", view.HarnessPermission);
        Assert.Null(view.ReviewStatus); Assert.Equal(0, f.ReviewCalls); Assert.Equal(0, Counts(f)[3]);
    }
    static WorkflowOutcome DisplayOnly() => new("task", "run", ComprehensionStatus.Unavailable, null, null,
        HumanGateStatus.Blocked, ["source-v1:untrusted-display-claim"], [], [], "blocked");
    [Fact]
    public void ProjectionContainsOnlyBoundedDisplayDataAndCannotRehydrateProof()
    {
        // Deliberately display-only negative input, not a substitute for the real-engine cases above.
        var view = LocalSoloOutcomeMapper.FromControlledWorkflow(DisplayOnly());
        Assert.Empty(view.Evidence); Assert.Null(view.Harness); Assert.Null(view.ExecutionId); NonExecutable(view);
        var text = Present(view); Assert.Contains("Harness: Unavailable", text); Assert.Contains("Advisory assessment: Unavailable", text);
        var secret = "password=private-value";
        view = LocalSoloOutcomeMapper.FromControlledWorkflow(DisplayOnly() with {
            ExternalResult = new("task", "run", ExternalExecutionStatus.Partial, new("execution", "OtherHarness"),
                secret + "\u001b[2J", Enumerable.Repeat(new string('x', 10000), 20).ToArray(), [], Requests: 3) });
        Assert.Equal("OtherHarness", view.Harness); Assert.Empty(view.Evidence);
        text = Present(view); Assert.DoesNotContain("private-value", text); Assert.DoesNotContain('\u001b', text);
        Assert.Contains("[truncated]", text); Assert.True(text.Length < 50000);
        Assert.DoesNotContain("Adapter request count", text); Assert.Contains("Adapter request count", Present(view, true));
        Assert.All(typeof(LocalResultView).GetProperties(), p => Assert.NotEqual(typeof(VerifiedSourceEvidence), p.PropertyType));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<VerifiedSourceEvidence>("{}"));
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void MismatchedOutcomeCorrelationCannotBecomeAView(bool wrongReview)
    {
        var outcome = DisplayOnly() with { ExternalResult = new("task", "run", ExternalExecutionStatus.Completed,
            new("execution", "OtherHarness"), "summary", [], []) };
        outcome = wrongReview ? outcome with { Review = new("other", "execution", EvidenceAssessmentStatus.SufficientEvidence, "claim", [], []) }
            : outcome with { ExternalResult = outcome.ExternalResult! with { WorkflowRunId = "other" } };
        Assert.Throws<ArgumentException>(() => LocalSoloOutcomeMapper.FromControlledWorkflow(outcome));
    }
    public void Dispose() => fixtures.Dispose();
}
