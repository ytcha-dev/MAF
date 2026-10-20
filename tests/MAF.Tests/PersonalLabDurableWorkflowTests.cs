using System.Text.Json;
using MAF.Agents.Review;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Tools;
using MAF.Host;
using MAF.Workflows.External;
using Microsoft.Agents.AI.Workflows;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class PersonalLabDurableWorkflowTests(ITestOutputHelper output) : IDisposable
{
    readonly SourceCandidateIntegrationTests fixtures = new(output); // Path strings only; no eager allocation.
    [Theory]
    [InlineData("valid")] [InlineData("lost-create")] [InlineData("lost-prompt")]
    [InlineData("reviewer-unavailable")] [InlineData("missing")]
    public async Task DurableOfflineBoundaryFeedsExistingOfficialWorkflow(string variant)
    {
        using var f = await fixtures.Fixture(variant: variant == "missing" ? "none" : "valid", operationTimeout: TimeSpan.FromSeconds(5));
        var p = LocalExecutionPreparation.Prepare(PersonalLabAdmissionTests.Input(f.F.Runtime.Root) with { StageTimeout = TimeSpan.FromSeconds(5) });
        f.F.Runtime.PreparedTask = p.Task; // Original identity before admission; same 5s policy scope.
        var lab = PersonalLabAdmission.AdmitOffline(p, p.Binding, "runtime-execution", f.F.Clock);
        var handle = f.F.Runtime.Inputs.PreparedHandle;
        var storage = Directory.CreateTempSubdirectory("maf-lab-journal-store-");
        PersonalLabOfflineOperation? op = null;
        try
        {
            op = PersonalLabOfflineOperation.Begin(p, lab, handle, storage.FullName);
            f.Fake.LoseCreateResponse = variant == "lost-create"; f.Fake.LoseSubmitResponse = variant == "lost-prompt";
            string? observedSession = null;
            var transform = f.ResponseTransform;
            f.ResponseTransform = (request, response) =>
            {
                response = transform!(request, response);
                if (request.Method == HttpMethod.Post && request.RequestUri!.AbsolutePath == "/session")
                {
                    using var body = JsonDocument.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());
                    observedSession = body.RootElement.GetProperty("id").GetString();
                }
                return response;
            };
            f.OfflineEffectObserver = (request, token) =>
            {
                var effect = request.Method == HttpMethod.Get ?
                    request.RequestUri!.AbsolutePath is "/global/health" or "/doc" or "/agent" ? LabEffect.HarnessValidation : LabEffect.Observe :
                    request.RequestUri!.AbsolutePath == "/session" ? LabEffect.CreateSession :
                    request.RequestUri.AbsolutePath.EndsWith("/prompt_async", StringComparison.Ordinal) ? LabEffect.SubmitPrompt :
                    throw new InvalidOperationException("OfflinePostNotPermitted");
                var attempt = op.Reserve(effect, token: token);
                return returned =>
                {
                    if (returned) attempt.RecordReturned(effect == LabEffect.CreateSession ? observedSession : null);
                    attempt.Dispose();
                };
            };
            using var pcaScript = new SyntheticProtectedHandoff.Chat(f);
            using var reviewScript = new SourceCandidateIntegrationTests.Reviewer();
            using var pca = new PersonalLabWorkflowTests.GuardedScript(pcaScript, p.Binding.Pca!, lab, p, LabEffect.PcaInference, op);
            using var review = new PersonalLabWorkflowTests.GuardedScript(reviewScript, p.Binding.Reviewer!, lab, p, LabEffect.ReviewerInference, op)
            { Unavailable = variant == "reviewer-unavailable" };
            var adapters = p.BindNativeAdapters(pca, review);
            var reader = new BoundedSourceReader(p.Task.Workspace, f.F.Clock);
            var flow = f.Flow(sources: new VerifiedSourceMode(reader), reviewer: adapters.Reviewer.ReviewAsync, briefing: adapters.Pca);
            var run = await flow.RunAsync(p.Task);
            var view = op.RecordOutcome(run.Outcome);
            Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" }, run.Events.OfType<ExecutorInvokedEvent>().Select(x => x.ExecutorId));
            Assert.Equal(p.Task.TaskId, view.TaskId); Assert.Equal(p.Task.WorkflowRunId, view.WorkflowRunId); Assert.Equal(handle.ExecutionId, view.ExecutionId);
            Assert.Equal(LocalViewKind.ControlledOfflineFixture, view.Kind); Assert.Equal(LocalHumanChoice.NotEstablished, view.HumanChoice);
            Assert.Equal(1, f.Fake.Requests.Count(x => x == "POST /session"));
            Assert.Equal(variant == "lost-create" ? 0 : 1, f.Fake.Requests.Count(x => x.EndsWith("/prompt_async")));
            Assert.DoesNotContain(f.Fake.Requests, x => x.EndsWith("/abort") || x.EndsWith("/reply"));
            if (variant == "lost-create") Assert.Equal(ExternalExecutionStatus.Unknown, view.Status);
            else Assert.Equal(ExternalExecutionStatus.Completed, view.Status);
            if (variant == "valid" || variant == "lost-prompt")
            { Assert.Equal(HumanGateStatus.ReadyForHumanReview, view.Readiness); Assert.Equal(2, reader.ReadAttempts); Assert.Equal(2, reviewScript.Calls); }
            if (variant == "reviewer-unavailable") Assert.Equal(EvidenceAssessmentStatus.ReviewUnavailable, view.ReviewStatus);
            if (variant == "missing") Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, view.ReviewStatus);
            op.Dispose();
            var read = PersonalLabOfflineOperation.Inspect(storage.FullName, p.OperationId); read.DemandExact(p, handle);
            Assert.Equal(view.Status, Assert.Single(read.Events, e => e.Kind == "ProviderObservation").Status);
            Assert.Equal(view.ReviewStatus, Assert.Single(read.Events, e => e.Kind == "ProviderObservation").Review);
            Assert.Equal(view.Readiness, Assert.Single(read.Events, e => e.Kind == "ProviderObservation").Gate);
            Assert.Equal("NotEstablished", read.Receipt); Assert.False(read.CanResume);
            Assert.Throws<ExternalHarnessException>(() => PersonalLabAdmission.Reserve(lab, PersonalLabAdmissionTests.Request(p)));
            if (variant is "lost-create" or "lost-prompt") Assert.Contains(read.Events, e => e.Kind == "Uncertain");
        }
        finally { op?.Dispose(); foreach (var file in storage.EnumerateFiles()) file.Delete(); storage.Delete(); }
    }
    public void Dispose() => fixtures.Dispose();
}
