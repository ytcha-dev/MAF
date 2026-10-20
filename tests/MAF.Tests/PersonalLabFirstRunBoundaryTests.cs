using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.Core;
using MAF.Core.Tools;
using MAF.Host;
using MAF.Workflows.External;
using Microsoft.Agents.AI.Workflows;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class PersonalLabFirstRunBoundaryTests(ITestOutputHelper output) : IDisposable
{
    readonly SourceCandidateIntegrationTests fixtures = new(output); // Paths only, no eager allocation.
    // Scripted OpenAI wire replies. The official native Agent executes the evidence tool;
    // this handler neither runs tools nor substitutes an orchestration/result object.
    internal sealed class NativeHttp(LocalExecutionPreparation p, bool review, bool unavailable) : HttpMessageHandler
    {
        public int Calls; public bool ToolResultObserved;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (unavailable) return new(HttpStatusCode.ServiceUnavailable) { Content = LabOpenCodeBoundaryTests.Json(new { error = new { message = "FixtureUnavailable", type = "server_error" } }) };
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            object message; string finish;
            if (!review)
            {
                var brief = new TaskBrief(p.Task.TaskId, p.Task.Workspace, p.Task.Objective, ExternalExecutionPolicy.ReadOnly, ComprehensionStatus.Proposed, [], []);
                message = new { role = "assistant", content = JsonSerializer.Serialize(brief, ExternalTaskResult.JsonOptions) }; finish = "stop";
            }
            else
            {
                var messages = body["messages"]!.AsArray();
                var data = messages.Last(m => m!["role"]!.GetValue<string>() == "user")!["content"]!;
                var text = data is JsonValue ? data.GetValue<string>() : string.Concat(data.AsArray().Select(x => x?["text"]?.GetValue<string>()));
                using var evidence = JsonDocument.Parse(text["Untrusted task data: ".Length..]);
                var refs = evidence.RootElement.GetProperty("Sources").EnumerateArray().Select(s => s.GetProperty("Reference").GetString()!).ToArray();
                ToolResultObserved = messages.Any(m => m!["role"]!.GetValue<string>() == "tool" && m["content"] is not null);
                if (!ToolResultObserved)
                {
                    message = new { role = "assistant", tool_calls = refs.Select((r, i) => new { id = "call_evidence" + i, type = "function",
                        function = new { name = "ReadEvidence", arguments = JsonSerializer.Serialize(new { reference = r, startLine = 120, endLine = 121 }) } }).ToArray() };
                    finish = "tool_calls";
                }
                else
                {
                    message = new { role = "assistant", content = JsonSerializer.Serialize(new { status = "SufficientEvidence",
                        summary = "Bounded source observations support this advisory conclusion.", evidenceReferences = refs, limitations = Array.Empty<string>() }) };
                    finish = "stop";
                }
            }
            return new(HttpStatusCode.OK) { Content = LabOpenCodeBoundaryTests.Json(new { id = "chatcmpl_fixture", @object = "chat.completion", created = 1,
                model = body["model"]!.GetValue<string>(), choices = new[] { new { index = 0, message, finish_reason = finish } } }) };
        }
    }
    [Theory]
    [InlineData("valid")] [InlineData("lost-create")] [InlineData("lost-prompt")]
    [InlineData("missing")] [InlineData("reviewer-unavailable")]
    [InlineData("changed-model")] [InlineData("missing-model")]
    public async Task OriginalSessionModelPinAndOwnedNativeClientsUseOfficialWorkflow(string variant)
    {
        using var f = await fixtures.Fixture(variant: variant == "missing" ? "none" : "valid", operationTimeout: TimeSpan.FromSeconds(5));
        var input = PersonalLabAdmissionTests.Input(f.F.Runtime.Root);
        var p = LocalExecutionPreparation.Prepare(input with { StageTimeout = TimeSpan.FromSeconds(5),
            Pca = input.Pca! with { Provider = "openai" }, Reviewer = input.Reviewer! with { Provider = "openai" } });
        f.F.Runtime.PreparedTask = p.Task;
        var handle = f.F.Runtime.Inputs.PreparedHandle;
        var admission = PersonalLabAdmission.AdmitOffline(p, p.Binding, handle.ExecutionId, f.F.Clock);
        var storage = Directory.CreateTempSubdirectory("maf-lab-original-response-");
        PersonalLabOfflineOperation? op = null;
        try
        {
            op = PersonalLabOfflineOperation.Begin(p, admission, handle, storage.FullName);
            var boundary = LabOpenCodeBoundary.ForTests(op, LabOpenCodeBoundaryTests.Origin);
            boundary.DemandIdentity(p.Task, handle); f.OfflineBoundary = boundary;
            f.Fake.LoseCreateResponse = variant == "lost-create"; f.Fake.LoseSubmitResponse = variant == "lost-prompt";
            JsonNode? sentPrompt = null;
            f.OfflineEffectObserver = (request, _) =>
            {
                if (request.RequestUri!.AbsolutePath.EndsWith("/prompt_async"))
                    sentPrompt = JsonNode.Parse(request.Content!.ReadAsStringAsync().GetAwaiter().GetResult());
                return _ => { }; // Observation only; boundary owns all reservations.
            };
            var transform = f.ResponseTransform!;
            f.ResponseTransform = (request, response) =>
            {
                response = transform(request, response);
                if (request.RequestUri!.AbsolutePath.EndsWith("/message"))
                {
                    var messages = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!.AsArray();
                    foreach (var item in messages)
                    {
                        item!["info"]!["providerID"] = p.Binding.HarnessRecipient!.Provider;
                        if (variant == "missing-model") item["info"]!.AsObject().Remove("modelID");
                        else item["info"]!["modelID"] = variant == "changed-model" ? "other" : p.Binding.HarnessRecipient.Model;
                    }
                    response.Content = new StringContent(messages.ToJsonString());
                }
                return response;
            };
            var pcaHttp = new NativeHttp(p, false, false); var reviewerHttp = new NativeHttp(p, true, variant == "reviewer-unavailable");
            using var pca = BoundNativeRecipient.ForTests(op, NativeModelRole.Pca, pcaHttp);
            using var reviewer = BoundNativeRecipient.ForTests(op, NativeModelRole.Reviewer, reviewerHttp);
            var adapters = BoundNativeRecipient.BindAdapters(op, pca, reviewer);
            var reader = new BoundedSourceReader(p.Task.Workspace, f.F.Clock);
            var run = await f.Flow(sources: new VerifiedSourceMode(reader), reviewer: adapters.Reviewer.ReviewAsync, briefing: adapters.Pca).RunAsync(p.Task);
            var view = op.RecordOutcome(run.Outcome);
            output.WriteLine($"{variant}: status={view.Status}; error={run.Outcome.ExternalResult?.ErrorCode}; review={view.ReviewStatus}; requests={string.Join(',', f.Fake.Requests)}");
            Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" }, run.Events.OfType<ExecutorInvokedEvent>().Select(e => e.ExecutorId));
            Assert.Equal(p.Task.TaskId, view.TaskId); Assert.Equal(p.Task.WorkflowRunId, view.WorkflowRunId); Assert.Equal(handle.ExecutionId, view.ExecutionId);
            Assert.Equal(LocalViewKind.ControlledOfflineFixture, view.Kind); Assert.Equal(LocalHumanChoice.NotEstablished, view.HumanChoice);
            Assert.Equal(1, pcaHttp.Calls);
            Assert.Equal(1, f.Fake.Requests.Count(r => r == "POST /session"));
            Assert.Equal(variant == "lost-create" ? 0 : 1, f.Fake.Requests.Count(r => r.EndsWith("/prompt_async")));
            Assert.DoesNotContain(f.Fake.Requests, r => r.EndsWith("/abort") || r.EndsWith("/reply") || r.EndsWith("/shell"));
            if (variant is "lost-create" or "changed-model" or "missing-model")
            { Assert.NotEqual(ExternalExecutionStatus.Completed, view.Status); Assert.Equal(0, reviewerHttp.Calls); }
            else Assert.Equal(ExternalExecutionStatus.Completed, view.Status);
            if (variant is "valid" or "lost-prompt")
            { Assert.Equal(HumanGateStatus.ReadyForHumanReview, view.Readiness); Assert.True(reviewerHttp.ToolResultObserved); Assert.Equal(2, reader.ReadAttempts); }
            if (variant == "missing") Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, view.ReviewStatus);
            if (variant == "reviewer-unavailable") Assert.Equal(EvidenceAssessmentStatus.ReviewUnavailable, view.ReviewStatus);
            if (sentPrompt is not null) LabOpenCodeBoundary.DemandModel(sentPrompt.AsObject(), p.Binding.HarnessRecipient!);
            op.Dispose();
            var journal = PersonalLabOfflineOperation.Inspect(storage.FullName, p.OperationId); journal.DemandExact(p, handle);
            Assert.False(journal.CanResume); Assert.Equal("NotEstablished", journal.HumanAcceptance);
            Assert.Equal(variant == "lost-create" ? "NotEstablished" : "FixtureVerifiedOriginalResponse / NotProductionAuthenticity", journal.Receipt);
            if (variant is "lost-create" or "lost-prompt") Assert.Contains(journal.Events, e => e.Kind == "Uncertain");
            Assert.Throws<ExternalHarnessException>(() => p.DemandExecution()); // Still PersonalLabNotApproved.
        }
        finally { op?.Dispose(); foreach (var file in storage.EnumerateFiles()) file.Delete(); storage.Delete(); }
    }
    public void Dispose() => fixtures.Dispose();
}
