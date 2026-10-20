using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Policy;
using MAF.Host;
using Microsoft.Agents.AI.Workflows;

namespace MAF.Tests;

public sealed class IndependentLabRuntimeTests(Xunit.Abstractions.ITestOutputHelper output)
{
    // Explicitly allocated by file-backed cases only; no SQLite or protected authority fixture.
    sealed class Owned : IDisposable
    {
        public readonly string Root, Workspace, Storage;
        public readonly LocalExecutionPreparation Preparation;
        public readonly PersonalLabAdmission Admission;
        public readonly ExternalExecutionHandle Handle = new("lab-execution", "OpenCode", ExternalRequestId: "msg_lab");
        public readonly PersonalLabOfflineOperation Operation;
        public readonly Wire Transport;
        public Owned(string variant, TimeProvider? clock = null)
        {
            Root = Path.Combine(Path.GetTempPath(), "maf-independent-lab", variant, Guid.NewGuid().ToString("N"));
            Workspace = Directory.CreateDirectory(Path.Combine(Root, "workspace")).FullName;
            Storage = Directory.CreateDirectory(Path.Combine(Root, "storage")).FullName;
            File.WriteAllText(Path.Combine(Workspace, "Example.cs"), string.Join('\n', Enumerable.Repeat("// padding", 119)) + "\nreturn 42;\n// end");
            var input = PersonalLabAdmissionTests.Input(Workspace);
            Preparation = LocalExecutionPreparation.Prepare(input with { StageTimeout = TimeSpan.FromSeconds(5),
                Pca = input.Pca! with { Provider = "openai" }, Reviewer = input.Reviewer! with { Provider = "openai" } });
            Admission = PersonalLabAdmission.AdmitOffline(Preparation, Preparation.Binding, Handle.ExecutionId, clock ?? TimeProvider.System);
            Operation = PersonalLabOfflineOperation.Begin(Preparation, Admission, Handle, Storage);
            Transport = new(Preparation, variant);
        }
        public void Dispose()
        {
            Operation.Dispose();
            foreach (var directory in new[] { Workspace, Storage })
            { foreach (var file in Directory.EnumerateFiles(directory)) File.Delete(file); Directory.Delete(directory); }
            Directory.Delete(Root); // only this explicit run; no parent/global/recursive cleanup
        }
    }
    sealed class Wire(LocalExecutionPreparation p, string variant) : DelegatingHandler(new RuntimeHttpFixture(p.Task.Workspace)
        { LoseCreateResponse = variant == "lost-create", LoseSubmitResponse = variant == "lost-prompt" })
    {
        public RuntimeHttpFixture Fake => (RuntimeHttpFixture)InnerHandler!;
        public int Disposals;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken t)
        {
            var response = await base.SendAsync(r, t); response.RequestMessage = r; // explicit controlled exchange association
            if (r.RequestUri!.AbsolutePath == "/global/health") response.Content = LabOpenCodeBoundaryTests.Json(new { healthy = true, version = "1.19.0" });
            if (r.Method == HttpMethod.Post && r.RequestUri.AbsolutePath == "/session" && variant == "invalid-receipt")
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(t))!; body["directory"] = "other";
                response.Content = new StringContent(body.ToJsonString());
            }
            if (r.RequestUri.AbsolutePath == "/session/ses_runtime" && variant == "wrong-session")
            {
                var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(t))!; body["id"] = "ses_other";
                response.Content = new StringContent(body.ToJsonString());
            }
            if (r.RequestUri.AbsolutePath.EndsWith("/message"))
            {
                var original = JsonNode.Parse(await response.Content.ReadAsStringAsync(t))!;
                var info = original[0]!["info"]!;
                info["providerID"] = p.Binding.HarnessRecipient!.Provider;
                info["modelID"] = variant == "changed-model" ? "other" : p.Binding.HarnessRecipient.Model;
                if (variant == "changed-provider") info["providerID"] = "other";
                if (variant != "missing")
                {
                    var toolInfo = info.DeepClone(); toolInfo["id"] = "msg_tools"; toolInfo["finish"] = "tool-calls";
                    original.AsArray().Insert(0, new JsonObject { ["info"] = toolInfo,
                        ["parts"] = new JsonArray(SourceCandidateIntegrationTests.Tool(Path.Combine(p.Task.Workspace,"Example.cs"), "return 42;\n// end", 0)) });
                }
                response.Content = new StringContent(original.ToJsonString());
            }
            return response;
        }
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    [Theory]
    [InlineData("valid")] [InlineData("lost-create")] [InlineData("lost-prompt")]
    [InlineData("invalid-receipt")] [InlineData("changed-model")] [InlineData("changed-provider")]
    [InlineData("wrong-session")] [InlineData("reviewer-unavailable")] [InlineData("missing")]
    public async Task LabOwnedJourneyNeedsNoProtectedAuthorityOrSqlite(string variant)
    {
        using var f = new Owned(variant);
        var p = f.Preparation;
        var pcaWire = new PersonalLabFirstRunBoundaryTests.NativeHttp(p, false, false);
        var reviewWire = new PersonalLabFirstRunBoundaryTests.NativeHttp(p, true, variant == "reviewer-unavailable");
        using var pca = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Pca, pcaWire);
        using var reviewer = BoundNativeRecipient.ForTests(f.Operation, NativeModelRole.Reviewer, reviewWire);
        using (var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, f.Transport, LabOpenCodeBoundaryTests.Origin))
        {
            var run = await runtime.RunAsync(pca, reviewer);
            Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" }, run.Events.OfType<ExecutorInvokedEvent>().Select(x => x.ExecutorId));
            Assert.Single(run.Events.OfType<WorkflowOutputEvent>());
            var result = run.Outcome.ExternalResult!;
            Assert.Equal(p.Task.TaskId, result.TaskId); Assert.Equal(p.Task.WorkflowRunId, result.WorkflowRunId);
            Assert.Equal(f.Handle.ExecutionId, result.Handle.ExecutionId); Assert.Equal(f.Handle.ExternalRequestId, result.Handle.ExternalRequestId);
            var success = variant is "valid" or "lost-prompt";
            if (success) { Assert.Equal(ExternalExecutionStatus.Completed, result.Status); Assert.Equal(HumanGateStatus.ReadyForHumanReview, run.Outcome.HumanGate); Assert.True(reviewWire.ToolResultObserved); }
            if (variant is "lost-create" or "invalid-receipt" or "changed-model" or "changed-provider" or "wrong-session")
                Assert.Equal(ExternalExecutionStatus.Unknown, result.Status);
            if (variant == "missing") Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, run.Outcome.Review!.Status);
            if (variant == "reviewer-unavailable") Assert.Equal(EvidenceAssessmentStatus.ReviewUnavailable, run.Outcome.Review!.Status);
            output.WriteLine($"{variant}: status={result.Status}; error={result.ErrorCode}; review={run.Outcome.Review?.Status}; requests={string.Join(',', f.Transport.Fake.Requests)}");
            if (variant != "lost-create") Assert.ThrowsAny<Exception>(() => f.Operation.Reserve(LabEffect.CreateSession));
            if (variant == "invalid-receipt") Assert.Throws<ExternalHarnessException>(() => f.Operation.RecordOutcome(run.Outcome));
            var view = variant == "invalid-receipt" ? LocalSoloOutcomeMapper.FromControlledWorkflow(run.Outcome) : f.Operation.RecordOutcome(run.Outcome);
            Assert.Equal(LocalViewKind.ControlledOfflineFixture, view.Kind); Assert.Equal(LocalHumanChoice.NotEstablished, view.HumanChoice);
            var counts = f.Transport.Fake.Requests;
            Assert.Equal(1, counts.Count(x => x == "POST /session"));
            Assert.Equal(variant is "lost-create" or "invalid-receipt" or "wrong-session" ? 0 : 1, counts.Count(x => x.EndsWith("/prompt_async")));
            Assert.DoesNotContain(counts, x => x.EndsWith("/abort") || x.EndsWith("/reply") || x.EndsWith("/shell"));
            Assert.Same(run, await runtime.RunAsync(pca, reviewer));
            f.Operation.Dispose();
            var journal = PersonalLabOfflineOperation.Inspect(f.Storage, p.OperationId); journal.DemandExact(p, f.Handle);
            Assert.False(journal.CanResume); Assert.Equal("NotEstablished", journal.HumanAcceptance);
            Assert.Single(journal.Events, e => e.Kind == "Intent" && e.Effect == LabEffect.CreateSession);
            if (success)
            {
                Assert.Equal("FixtureVerifiedOriginalResponse / NotProductionAuthenticity", journal.Receipt);
                Assert.Single(journal.Events, e => e.Kind == "Intent" && e.Effect == LabEffect.SubmitPrompt);
                Assert.Single(view.Evidence);
                Assert.Equal("ses_runtime", result.Handle.ExternalSessionId);
            }
            Assert.Throws<ExternalHarnessException>(() => p.DemandHarnessRecipient(p.Binding.HarnessRecipient));
            Assert.Throws<ExternalHarnessException>(p.DemandExecution);
            Assert.DoesNotContain(Directory.EnumerateFiles(f.Storage), x => x.EndsWith(".db"));
        }
        Assert.Equal(1, f.Transport.Disposals);
    }
    [Fact]
    public void WrongExecutionCannotOpenLabJournalOrAuthorizeProduction()
    {
        var p = LocalExecutionPreparation.Prepare(PersonalLabAdmissionTests.Input());
        var admission = PersonalLabAdmission.AdmitOffline(p, p.Binding, "original", TimeProvider.System);
        Assert.Throws<ExternalHarnessException>(() => PersonalLabOfflineOperation.Begin(p, admission,
            new("wrong", "OpenCode", ExternalRequestId: "msg_lab"), "unused"));
        Assert.Throws<ExternalHarnessException>(p.DemandExecution); // No filesystem allocation.
    }
    [Fact]
    public async Task LabJournalAndAdmissionDoNotGrantRawProtectedClientOrHostAccess()
    {
        using var f = new Owned("protected-isolation");
        using var http = new HttpClient(f.Transport);
        var client = new MAF.Workflows.External.OpenCode.OpenCodeClient(http,
            new(LabOpenCodeBoundaryTests.Origin, f.Preparation.Task.Workspace, AllowLoopbackHttp: true));
        var error = await Assert.ThrowsAsync<ExternalHarnessException>(() => client.ValidateAsync(
            new(f.Preparation.Task.TaskId, f.Preparation.Task.Workspace, f.Preparation.Task.Objective, f.Preparation.Task.StageTimeout), default));
        Assert.Equal("ProtectedRuntimeRequired", error.Code); Assert.Empty(f.Transport.Fake.Requests);
        using var text = new StringWriter(); using var errors = new StringWriter();
        Assert.Equal(1, LocalSoloConsole.Run(["--local", "submit"], text, errors, _ => throw new InvalidOperationException()));
        Assert.Contains("ProductionTrustUnavailable", errors.ToString());
        Assert.Equal("ProductionTrustUnavailable", (await Assert.ThrowsAsync<ExternalHarnessException>(() =>
            ExternalConsole.RunAsync(["--external-task"], "unused", _ => throw new InvalidOperationException()))).Code);
    }
    sealed class CountingBoundary : DelegatingHandler
    {
        public int Disposals;
        protected override void Dispose(bool disposing) { if (disposing) Disposals++; base.Dispose(disposing); }
    }
    [Fact]
    public async Task HistoricalLazyClientPublishesOneChainBeforeQuiescentDisposal()
    {
        var directory = Path.Combine(Path.GetTempPath(), "maf-independent-lab", "lazy-client-concurrency", Guid.NewGuid().ToString("N"));
        // Existing fixture needs a real root, but no new Lab journal/source/SQLite initialization is needed here.
        var fixture = new SyntheticProtectedHandoff(Path.Combine(directory, "synthetic.db"));
        var boundary = new CountingBoundary(); fixture.OfflineBoundary = boundary;
        using var barrier = new Barrier(9);
        try
        {
            var tasks = Enumerable.Range(0, 8).Select(_ => Task.Factory.StartNew(() =>
            { if (!barrier.SignalAndWait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Barrier"); return fixture.Http; },
                CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToArray();
            Assert.True(barrier.SignalAndWait(TimeSpan.FromSeconds(10)));
            var clients = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(clients, c => Assert.Same(clients[0], c)); Assert.NotNull(boundary.InnerHandler);
            Assert.Equal(0, boundary.Disposals);
            fixture.Dispose(); fixture.Dispose(); Assert.Equal(1, boundary.Disposals);
        }
        finally { fixture.Dispose(); Directory.Delete(directory); }
        // Supported lifecycle: joins finish before Dispose. Concurrent Dispose/Get and post-disposal use are unsupported.
    }
    [Theory]
    [InlineData("task")] [InlineData("run")] [InlineData("revoked")] [InlineData("journal-failure")] [InlineData("network")] [InlineData("expired")] [InlineData("missing-receipt")]
    public async Task InvalidOrUnavailableLabCannotReachInnerTransport(string variant)
    {
        var clock = new PersonalLabAdmissionTests.Clock();
        using var f = new Owned(variant, clock);
        if (variant == "network")
        {
            Assert.Throws<ExternalHarnessException>(() => PersonalLabOfflineRuntime.ForTests(f.Operation, f.Transport,
                LabOpenCodeBoundaryTests.Origin, LabEffectTarget.Network));
            Assert.Empty(f.Transport.Fake.Requests); f.Transport.Dispose(); return;
        }
        using var runtime = PersonalLabOfflineRuntime.ForTests(f.Operation, f.Transport, LabOpenCodeBoundaryTests.Origin);
        if (variant == "missing-receipt")
        {
            Assert.Equal("LabReceiptRequired", Assert.Throws<ExternalHarnessException>(() => f.Operation.Reserve(LabEffect.SubmitPrompt)).Code);
            Assert.Equal("LabReceiptRequired", Assert.Throws<ExternalHarnessException>(() => f.Operation.Reserve(LabEffect.Observe)).Code);
            Assert.Empty(f.Transport.Fake.Requests); return;
        }
        if (variant == "revoked") f.Operation.Revoke();
        if (variant == "expired") clock.Advance = TimeSpan.FromMinutes(1);
        if (variant == "journal-failure") ((FileStream)typeof(PersonalLabOfflineOperation).GetField("journal", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(f.Operation)!).Dispose();
        var task = f.Preparation.Task;
        var request = new ExternalTaskRequest(variant == "task" ? "other" : task.TaskId, task.Workspace, task.Objective, task.StageTimeout);
        if (variant == "journal-failure")
        {
            var failed = await runtime.DelegateAsync(request, task.WorkflowRunId, default);
            Assert.NotEqual(ExternalExecutionStatus.Completed, failed.Status);
            Assert.ThrowsAny<Exception>(() => f.Operation.Reserve(LabEffect.HarnessValidation));
        }
        else if (variant is "task" or "run" or "revoked" or "expired")
            await Assert.ThrowsAnyAsync<Exception>(() => runtime.DelegateAsync(request, variant == "run" ? "other" : task.WorkflowRunId, default));
        Assert.Empty(f.Transport.Fake.Requests);
    }
}
