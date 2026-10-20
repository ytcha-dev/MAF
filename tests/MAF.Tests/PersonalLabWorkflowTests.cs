using MAF.ApprovalAcceptance;
using System.Text.Json;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Core.Tools;
using MAF.Host;
using MAF.Workflows.External;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace MAF.Tests;

// Composition belongs to tests, not Host. Constructor creates no directories or clients.
public sealed class PersonalLabWorkflowTests(ITestOutputHelper output) : IDisposable
{
    readonly SourceCandidateIntegrationTests fixtures = new(output);

    [Fact]
    public void RemovedWorkspaceFailsBeforeAnyEffectReservation()
    {
        var owned = Directory.CreateTempSubdirectory("maf-s2d-workspace-");
        try
        {
            var p = LocalExecutionPreparation.Prepare(PersonalLabAdmissionTests.Input(owned.FullName));
            var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", TimeProvider.System);
            lab.BeginOffline(); owned.Delete(); // Exact owned empty directory; no recursive cleanup.
            Assert.Throws<ArgumentException>(() => PersonalLabAdmission.Reserve(lab, PersonalLabAdmissionTests.Request(p)));
            Assert.Equal(0, lab.ReservedEffects); Assert.Empty(lab.Observations);
        }
        finally { if (Directory.Exists(owned.FullName)) owned.Delete(); }
    }

    [Theory]
    [InlineData("valid")] [InlineData("missing")] [InlineData("partial")]
    [InlineData("reviewer-unavailable")] [InlineData("pending")]
    [InlineData("lost-create")] [InlineData("lost-prompt")] [InlineData("lost-prompt-observed")]
    [InlineData("revoked")] [InlineData("cancelled")] [InlineData("protected-denied")]
    public async Task ExactPreparedAdmissionGuardsExistingOfficialJourney(string variant)
    {
        // Only these integration cases need real source metadata/content and disposable SQLite.
        using var f = await fixtures.Fixture(sources: variant == "partial" ? 2 : 1,
            variant: variant == "missing" ? "none" : "valid");
        var p = LocalExecutionPreparation.Prepare(PersonalLabAdmissionTests.Input(f.F.Runtime.Root));
        f.F.Runtime.PreparedTask = p.Task; // Original identity BEFORE admission/Workflow, never after it.
        using var stop = new CancellationTokenSource();
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, f.F.Runtime.Inputs.PreparedHandle.ExecutionId, f.F.Clock, stop.Token);
        Assert.Equal(PersonalLabState.Admitted, lab.State); Assert.Empty(f.Fake.Requests);
        Assert.Null(await f.F.Executions.GetAsync(p.Task.TaskId));
        f.Fake.LoseCreateResponse = variant == "lost-create";
        f.Fake.LoseSubmitResponse = variant is "lost-prompt" or "lost-prompt-observed";
        f.Fake.Pending = variant == "pending";
        if (variant == "protected-denied")
        {
            f.F.Policy = f.F.Policy with { Epoch = new(2), Version = new(2), Rules = [..f.F.Policy.Rules.Select(r =>
                r.Operation == PolicyOperation.SubmitNewReadOnlyTask ? r with { Outcome = PolicyOutcome.Deny } : r)] };
            await f.F.Store().ActivatePolicyAsync();
        }
        if (variant == "lost-prompt")
        {
            var sourceTransform = f.ResponseTransform;
            f.ResponseTransform = (request, response) =>
            {
                response = sourceTransform!(request, response);
                if (request.RequestUri!.AbsolutePath == "/session/status")
                {
                    response.Content.Dispose();
                    response.Content = new StringContent(JsonSerializer.Serialize(new { ses_runtime = new { type = "unconfirmed" } }));
                }
                return response;
            };
        }
        f.OfflineEffectObserver = (request, token) =>
        {
            // Routes are fixture-only; an unexpected POST never reaches the fake handler.
            var route = request.RequestUri!.AbsolutePath;
            var effect = request.Method == HttpMethod.Post ? route switch
            {
                "/session" => LabEffect.CreateSession,
                "/session/ses_runtime/prompt_async" => LabEffect.SubmitPrompt,
                _ => throw new InvalidOperationException("OfflinePostNotPermitted")
            } : request.Method == HttpMethod.Get ? route switch
            {
                "/global/health" or "/doc" or "/agent" => LabEffect.HarnessValidation,
                "/session/ses_runtime" or "/session/ses_runtime/message" or "/permission" or "/question" or "/session/status" => LabEffect.Observe,
                _ => throw new InvalidOperationException("OfflineGetNotPermitted")
            } : throw new InvalidOperationException("OfflineMethodNotPermitted");
            var attempt = PersonalLabAdmission.Reserve(lab, PersonalLabAdmissionTests.Request(p, effect), token);
            return returned =>
            {
                if (returned) attempt.RecordReturned();
                attempt.Dispose();
                if (effect == LabEffect.CreateSession && variant == "revoked") lab.Revoke();
                if (effect == LabEffect.CreateSession && variant == "cancelled") stop.Cancel();
            };
        };
        using var pcaScript = new SyntheticProtectedHandoff.Chat(f);
        using var reviewerScript = new SourceCandidateIntegrationTests.Reviewer { Partial = variant == "partial" };
        using var pca = new GuardedScript(pcaScript, p.Binding.Pca!, lab, p, LabEffect.PcaInference);
        using var reviewer = new GuardedScript(reviewerScript, p.Binding.Reviewer!, lab, p, LabEffect.ReviewerInference)
        { Unavailable = variant == "reviewer-unavailable" };
        var adapters = p.BindNativeAdapters(pca, reviewer); // S2C independent role binding, no model call.
        var reader = new BoundedSourceReader(p.Task.Workspace, f.F.Clock);
        var flow = f.Flow(sources: new VerifiedSourceMode(reader), reviewer: adapters.Reviewer.ReviewAsync, briefing: adapters.Pca);
        lab.BeginOffline();
        var run = await flow.RunAsync(p.Task, stop.Token);
        lab.Consume();
        var view = LocalSoloOutcomeMapper.FromControlledWorkflow(run.Outcome);
        Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" },
            run.Events.OfType<ExecutorInvokedEvent>().Select(e => e.ExecutorId));
        Assert.Single(run.Events.OfType<WorkflowOutputEvent>());
        Assert.Equal(p.Task, f.Task); Assert.Equal(p.Task.TaskId, view.TaskId); Assert.Equal(p.Task.WorkflowRunId, view.WorkflowRunId);
        Assert.Equal(LocalViewKind.ControlledOfflineFixture, view.Kind);
        Assert.Equal(LocalHumanChoice.NotEstablished, view.HumanChoice);
        using var text = new StringWriter(); LocalSoloPresentation.Result(text, view);
        Assert.Contains("ControlledOfflineFixture / NotLiveProvider", text.ToString());
        Assert.DoesNotContain("Human result choice: Accepted", text.ToString());
        Assert.All(lab.Observations, o => Assert.Equal(p.OperationId, o.OperationId));
        Assert.DoesNotContain(f.Fake.Requests, r => r.EndsWith("/abort") || r.Contains("/reply"));
        Assert.Equal(1, f.PcaCalls); // Actual official native PCA with scripted transport.
        Assert.Equal("PersonalLabNotApproved", Assert.Throws<ExternalHarnessException>(p.DemandExecution).Code);

        var row = await f.F.Executions.GetAsync(p.Task.TaskId);
        if (variant == "protected-denied")
        {
            Assert.Null(row); Assert.Empty(f.Fake.Requests); Assert.Equal(HumanGateStatus.Blocked, view.Readiness);
            Assert.Null(view.ExecutionId); Assert.Null(view.ReviewStatus);
        }
        else
        {
            Assert.NotNull(row); Assert.Equal(row.Handle.ExecutionId, view.ExecutionId);
            Assert.Equal(p.Task.WorkflowRunId, row.WorkflowRunId);
            Assert.Equal(1, f.Fake.Requests.Count(r => r == "POST /session"));
            Assert.Equal(variant is "lost-create" or "revoked" or "cancelled" ? 0 : 1,
                f.Fake.Requests.Count(r => r.EndsWith("/prompt_async")));
            if (variant != "lost-create")
                Assert.Equal(variant == "cancelled" ? SessionReceiptStatus.Pending : SessionReceiptStatus.Verified,
                    (await f.F.Store().GetSessionReceiptAsync(p.Task.TaskId))!.Status);
        }
        switch (variant)
        {
            case "valid": case "lost-prompt-observed":
                Assert.Equal(ExternalExecutionStatus.Completed, view.Status);
                Assert.Equal(EvidenceAssessmentStatus.SufficientEvidence, view.ReviewStatus);
                Assert.Equal(HumanGateStatus.ReadyForHumanReview, view.Readiness);
                Assert.Single(view.Evidence); Assert.Equal(2, reader.ReadAttempts); Assert.Equal(2, reviewerScript.Calls);
                Assert.Contains("Example.cs:120", reviewerScript.ToolEvidence);
                Assert.Equal(p.Task.TaskId, run.Outcome.Review!.Sources!.Admitted.Single().Binding.TaskId);
                break;
            case "missing": case "partial":
                Assert.Equal(ExternalExecutionStatus.Completed, view.Status);
                Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, view.ReviewStatus);
                Assert.Equal(HumanGateStatus.NeedsHumanReview, view.Readiness); break;
            case "reviewer-unavailable":
                Assert.Equal(EvidenceAssessmentStatus.ReviewUnavailable, view.ReviewStatus);
                Assert.Equal(HumanGateStatus.NeedsHumanReview, view.Readiness); break;
            case "pending":
                Assert.Equal(ExternalExecutionStatus.WaitingForApproval, view.Status);
                Assert.Equal(HumanGateStatus.WaitingForApproval, view.Readiness);
                Assert.Equal("NotEstablished", view.MafApproval); break;
            case "lost-create": case "lost-prompt": case "revoked": case "cancelled":
                Assert.Equal(ExternalExecutionStatus.Unknown, view.Status);
                Assert.Null(view.ReviewStatus); Assert.Equal(0, reviewerScript.Calls); break;
        }
        var calls = f.Fake.Requests.Count; var modelCalls = f.PcaCalls + reviewerScript.Calls;
        Assert.Same(run, await flow.RunAsync(p.Task)); // Existing single-attempt Workflow cache, not new admission.
        Assert.Equal(calls, f.Fake.Requests.Count); Assert.Equal(modelCalls, f.PcaCalls + reviewerScript.Calls);
        Assert.Throws<ExternalHarnessException>(lab.BeginOffline);
        await Assert.ThrowsAsync<InvalidOperationException>(() => flow.RunAsync(p.Task with { Objective = "changed" }));
        output.WriteLine($"Variant={variant}; lab={lab.State}; reserved={lab.ReservedEffects}; observed={lab.Observations.Length}; fake HTTP={calls}; real HTTP/model calls=0");
    }

    sealed class GuardedScript(IChatClient script, NativeRecipientBinding binding, PersonalLabAdmission admission,
        LocalExecutionPreparation prepared, LabEffect effect) : DelegatingChatClient(script)
    {
        public bool Unavailable { get; init; }
        public override object? GetService(Type type, object? key = null) => type == typeof(ChatClientMetadata)
            ? new ChatClientMetadata(binding.Provider, binding.Endpoint, binding.Model) : base.GetService(type, key);
        public override async Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            using var attempt = PersonalLabAdmission.Reserve(admission, PersonalLabAdmissionTests.Request(prepared, effect), cancellationToken);
            if (Unavailable) throw new InvalidOperationException("ScriptedReviewerUnavailable");
            var response = await base.GetResponseAsync(messages, options, cancellationToken);
            attempt.RecordReturned(); return response;
        }
        public override IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("OfflineStreamingNotSupported");
    }
    public void Dispose() => fixtures.Dispose(); // Existing exact-owned fixture cleanup; no lazy initialization.
}
