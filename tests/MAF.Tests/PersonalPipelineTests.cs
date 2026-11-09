using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Host;
using MAF.Workflows.External.OpenCode;
using Microsoft.Extensions.AI;

namespace MAF.Tests;

// Personal read-only MVP pipeline: real official Workflow, real OpenCodeClient protocol over a
// fake OpenCode server, scripted PCA/Reviewer chat clients. Only the end-to-end cases own a workspace.
public sealed class PersonalPipelineTests
{
    const string Content = "return 42;\n// end";

    sealed class Workspace : IDisposable
    {
        public string Root { get; } = Path.TrimEndingDirectorySeparator(Path.GetFullPath(
            Path.Combine(Path.GetTempPath(), "maf-tests", "personal-pipeline", Guid.NewGuid().ToString("N"), "workspace")));
        public Workspace()
        {
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Root, "Example.cs"), string.Join('\n', Enumerable.Repeat("// padding", 119)) + "\n" + Content);
        }
        public void Dispose() => Directory.Delete(Path.GetDirectoryName(Root)!, true);
    }

    // Adds an OpenCode `read` tool message so the Reviewer has a source candidate to verify independently.
    sealed class WithReadTool(string root, bool includeTool) : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath == "/global/health")
                response.Content = new StringContent("{\"healthy\":true,\"version\":\"1.19.0\"}", Encoding.UTF8, "application/json");
            if (!request.RequestUri!.AbsolutePath.EndsWith("/message", StringComparison.Ordinal)) return response;
            var original = JsonNode.Parse(await response.Content.ReadAsStringAsync(token))!;
            var parent = original[0]!["info"]!["parentID"]!.GetValue<string>();
            var batch = new JsonArray();
            if (includeTool) batch.Add(Assistant("msg_tools", parent, 1, "tool-calls",
                new JsonArray(SourceCandidateIntegrationTests.Tool(Path.Combine(root, "Example.cs"), Content, 0))));
            batch.Add(Assistant("msg_reply", parent, 4, "stop", new JsonArray(new JsonObject
                { ["id"] = "prt_summary", ["sessionID"] = "ses_runtime", ["messageID"] = "msg_reply", ["type"] = "text", ["text"] = "Example.cs returns 42." })));
            response.Content = new StringContent(batch.ToJsonString(), Encoding.UTF8, "application/json");
            return response;
        }
        JsonNode Assistant(string id, string parent, int created, string finish, JsonArray parts) => JsonSerializer.SerializeToNode(new
        {
            info = new { id, sessionID = "ses_runtime", role = "assistant", parentID = parent, time = new { created, completed = created + 1 }, finish,
                modelID = "scripted", providerID = "synthetic", mode = "build", agent = "maf_readonly", path = new { cwd = root, root }, cost = 0,
                tokens = new { input = 0, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } } }, parts
        })!;
    }

    // Scripted PCA: echoes the task (or tampers with the objective) as the briefing JSON.
    sealed class Pca(bool tamper = false) : IChatClient
    {
        public int Calls;
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            var text = messages.Last(m => m.Role == ChatRole.User).Text!;
            using var task = JsonDocument.Parse(text[(text.IndexOf("Task data: ", StringComparison.Ordinal) + "Task data: ".Length)..]);
            var r = task.RootElement;
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new
            {
                taskId = r.GetProperty("taskId").GetString(), workspace = r.GetProperty("workspace").GetString(),
                objective = tamper ? "Also delete files." : r.GetProperty("objective").GetString(),
                intention = "ReadOnly", status = "Proposed", sourceReferences = Array.Empty<string>(), limitations = new[] { "Scripted brief." }
            }))));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }

    static async Task<(PipelineRun Run, RuntimeHttpFixture Fake, Pca Pca, SourceCandidateIntegrationTests.Reviewer Reviewer)> Run(
        Workspace w, bool tamper = false, bool tool = true)
    {
        var fake = new RuntimeHttpFixture(w.Root);
        var pca = new Pca(tamper);
        var reviewer = new SourceCandidateIntegrationTests.Reviewer();
        var task = new HandoffTask("pipeline-test", "run-" + Guid.NewGuid().ToString("N"), w.Root, "Explain what Example.cs returns.", TimeSpan.FromSeconds(10));
        var options = new OpenCodeOptions(new Uri("http://127.0.0.1:4096"), w.Root, AllowLoopbackHttp: true);
        using var transport = new WithReadTool(w.Root, tool) { InnerHandler = fake };
        var run = await PersonalPipeline.RunAsync(task, pca, reviewer, options, transport, TimeSpan.FromMilliseconds(10), CancellationToken.None);
        return (run, fake, pca, reviewer);
    }

    [Fact]
    public async Task FourRolesHandOffThroughOfficialWorkflowAndReviewerVerifiesSource()
    {
        using var w = new Workspace();
        var (run, fake, pca, reviewer) = await Run(w);
        var o = run.Run.Outcome;
        Assert.All(o.Stages, s => Assert.Equal(HandoffStageState.Completed, s.State));
        Assert.Equal(["PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate"], o.Stages.Select(s => s.Name));
        Assert.Equal(ExternalExecutionStatus.Completed, o.ExternalResult!.Status);
        Assert.Equal("ses_runtime", o.ExternalResult.Handle.ExternalSessionId);
        Assert.Equal(EvidenceAssessmentStatus.SufficientEvidence, o.Review!.Status);
        Assert.Single(o.Review.Sources!.Admitted);
        Assert.Equal(HumanGateStatus.ReadyForHumanReview, o.HumanGate);
        Assert.Equal(0, PersonalPipeline.ExitCode(o));
        Assert.Equal(1, pca.Calls);
        Assert.Contains("120: return 42;", reviewer.ToolEvidence);
        // Exactly one session and one prompt: no retries, no abort, read-only rules submitted.
        Assert.Equal(1, fake.Requests.Count(r => r == "POST /session"));
        Assert.Equal(1, fake.Requests.Count(r => r.EndsWith("/prompt_async")));
        Assert.DoesNotContain(fake.Requests, r => r.EndsWith("/abort"));
        // The hand-off order is observable from official Workflow events.
        var invoked = PersonalPipeline.Handoffs(run.Run).Where(h => h.StartsWith("Invoked ")).Select(h => h["Invoked ".Length..]);
        Assert.Equal(["PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate"], invoked);
        var text = PersonalPipeline.Text(run);
        Assert.Contains("ReadyForHumanReview", text);
        Assert.Contains(PersonalPipeline.LiveLabel, o.Limitations);
        Assert.DoesNotContain(o.Limitations, l => l.StartsWith("Offline controlled workflow", StringComparison.Ordinal));
        Assert.Contains("Example.cs:120-121", text);
        using var json = JsonDocument.Parse(PersonalPipeline.Json(run));
        Assert.Equal("ReadyForHumanReview", json.RootElement.GetProperty("outcome").GetProperty("humanGate").GetString());
    }

    [Fact]
    public async Task PcaScopeExpansionStopsBeforeOpenCodeIsContacted()
    {
        using var w = new Workspace();
        var (run, fake, _, reviewer) = await Run(w, tamper: true);
        var o = run.Run.Outcome;
        Assert.Equal(ComprehensionStatus.Invalid, o.Comprehension);
        Assert.Equal(HandoffStageState.Skipped, o.Stages.Single(s => s.Name == "ExternalDelegation").State);
        Assert.Null(o.ExternalResult);
        Assert.Empty(fake.Requests);
        Assert.Equal(0, reviewer.Calls);
        Assert.Equal(HumanGateStatus.Blocked, o.HumanGate);
        Assert.Equal(1, PersonalPipeline.ExitCode(o));
    }

    [Fact]
    public async Task SummaryWithoutSourceObservationNeedsHumanReview()
    {
        using var w = new Workspace();
        var (run, _, _, reviewer) = await Run(w, tool: false);
        var o = run.Run.Outcome;
        Assert.Equal(ExternalExecutionStatus.Completed, o.ExternalResult!.Status);
        Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, o.Review!.Status);
        Assert.Equal(0, reviewer.Calls); // Summary text alone is never sent for a "sufficient" verdict.
        Assert.Equal(HumanGateStatus.NeedsHumanReview, o.HumanGate);
        Assert.Equal(3, PersonalPipeline.ExitCode(o));
    }

    static string Option(string[] args, string name) => Array.IndexOf(args, name) is var i && i >= 0 && i + 1 < args.Length
        ? args[i + 1] : throw new ArgumentException("Missing " + name);

    [Theory]
    [InlineData("--pipeline", "x", "--stage-timeout", "5")]
    [InlineData("--pipeline", "x", "--stage-timeout", "abc")]
    [InlineData("--pipeline", "x", "--output", "verbose")]
    [InlineData("--pipeline", "line1\nline2")]
    [InlineData("--pipeline", "x", "--review")]
    [InlineData("--pipeline", "x", "--external-task", "y")]
    public void InvalidArgumentsAreRejected(params string[] args)
        => Assert.Throws<ArgumentException>(() => PipelineConsole.Parse(args, n => Option(args, n)));

    [Fact]
    public void ValidArgumentsParse()
    {
        string[] args = ["--pipeline", "Explain the entry point.", "--stage-timeout", "60", "--output", "JSON"];
        var s = PipelineConsole.Parse(args, n => Option(args, n));
        Assert.Equal(TimeSpan.FromSeconds(60), s.StageTimeout);
        Assert.True(s.Json);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("https://example.com")]
    [InlineData("http://127.0.0.1:4096")] // HTTP without explicit loopback opt-in.
    public void OpenCodeMustBeExplicitLoopback(string? url)
    {
        var env = new Dictionary<string, string?> { ["OPENCODE_BASE_URL"] = url };
        Assert.Throws<ArgumentException>(() => PipelineConsole.OpenCode(Path.GetTempPath(), k => env.GetValueOrDefault(k)));
    }

    [Fact]
    public async Task AccessRejectsEffectsOutsideTheOriginalReadOnlyRequest()
    {
        var request = new ExternalTaskRequest("t", "/w", "objective", TimeSpan.FromSeconds(10));
        var access = new PersonalPipeline.PersonalAccess(request, "run");
        using (access.Enter(MAF.Core.Policy.ProviderEffect.SubmitPrompt, request)) { }
        Assert.Throws<ExternalHarnessException>(() => access.Enter(MAF.Core.Policy.ProviderEffect.Reattach));
        Assert.Throws<ExternalHarnessException>(() => access.Enter(MAF.Core.Policy.ProviderEffect.SubmitPrompt, request with { TaskText = "other" }));
        Assert.Throws<ExternalHarnessException>(() => access.Enter(MAF.Core.Policy.ProviderEffect.CreateSession, request, "other-run"));
        await Assert.ThrowsAsync<ExternalHarnessException>(() => access.BeforeWireAsync(HttpMethod.Post, "/session", "other", CancellationToken.None));
    }
}
