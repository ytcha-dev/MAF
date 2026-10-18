using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAF.Agents.Review;
using MAF.ApprovalAcceptance;
using MAF.Core;
using MAF.Core.Tools;
using MAF.Core.Policy;
using MAF.Workflows.External;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Xunit.Abstractions;

namespace MAF.Tests;

public sealed class SourceEvidenceCoverageTests(ITestOutputHelper output) : IDisposable
{
    readonly string root = Path.Combine(Path.GetTempPath(), "maf-coverage-" + Guid.NewGuid().ToString("N"));
    public static IEnumerable<object[]> Scenarios() => new[] { "short", "over20", "middle", "default100", "default2000", "read-grep", "glob-read",
        "repeat", "conflict", "two", "truncated", "oversized", "long-line", "anchor-budget", "changed" }
        .SelectMany(s => new[] { "1.18.35", "1.19.0" }.Select(v => new object[] { s, v }));
    static string[] Lines(int count) => Enumerable.Range(1, count).Select(i => $"// line{i:D4}").ToArray();
    async Task<SyntheticProtectedHandoff> Fixture(string scenario, string version)
    {
        var f = new SyntheticProtectedHandoff(Path.Combine(root, "synthetic.db")); await f.Initialize();
        var lines = Lines(scenario is "default2000" or "oversized" ? 2000 : scenario is "middle" or "truncated" or "default100" ? 100 : scenario == "over20" ? 40 : 5);
        if (scenario == "long-line") lines = [new string('x', 2200), "// short second line"];
        if (scenario == "oversized") lines = Enumerable.Range(1, 80).Select(i => $"// {i:D4}" + new string('x', 200)).ToArray();
        if (scenario is "conflict-tail" or "two-excerpt") lines = Lines(40);
        if (scenario == "anchor-budget") lines = Enumerable.Repeat("// " + new string('x', 60), 20).ToArray();
        File.WriteAllText(Path.Combine(f.F.Runtime.Root, "Example.cs"), string.Join('\n', lines));
        File.WriteAllText(Path.Combine(f.F.Runtime.Root, "Second.cs"), "// second\nreturn 7;");
        if (scenario == "two-excerpt") File.WriteAllText(Path.Combine(f.F.Runtime.Root, "Second.cs"), string.Join('\n', lines));
        var start = scenario == "middle" ? 30 : 1; var limit = scenario is "middle" or "truncated" ? 10 : 2000;
        f.ResponseTransform = (request, response) =>
        {
            if (request.RequestUri!.AbsolutePath == "/global/health") response.Content = Json(new { healthy = true, version });
            if (!request.RequestUri.AbsolutePath.EndsWith("/message", StringComparison.Ordinal)) return response;
            var original = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            var parent = original[0]!["info"]!["parentID"]!.GetValue<string>(); var parts = new JsonArray();
            var read = Read(f, "Example.cs", lines, start, limit, 0); parts.Add(read);
            if (scenario == "two") parts.Add(Read(f, "Second.cs", ["// second", "return 7;"], 1, 2000, 1));
            if (scenario == "two-excerpt") parts.Add(Read(f, "Second.cs", lines, 1, 2000, 1));
            if (scenario == "conflict-tail")
            { var other = lines.ToArray(); other[^1] = "// changed tail"; parts.Add(Read(f, "Example.cs", other, start, limit, 1)); }
            if (scenario == "different-range") parts.Add(Read(f, "Example.cs", lines, 2, limit, 1));
            if (scenario is "repeat" or "conflict")
            { var again = Read(f, "Example.cs", lines, start, limit, 1); if (scenario == "conflict")
                { again["state"]!["metadata"]!["display"]!["text"] = "// different\n" + string.Join('\n', lines.Skip(1)); again["state"]!["metadata"]!["preview"] = again["state"]!["metadata"]!["display"]!["text"]!.DeepClone(); }
              parts.Add(again); }
            if (scenario == "read-grep") parts.Add(Other("grep", 1));
            if (scenario == "glob-read") parts.Insert(0, Other("glob", 1));
            if (scenario.StartsWith("bad-", StringComparison.Ordinal))
            {
                var other = Other("grep", 1); var state = other["state"]!;
                switch (scenario)
                {
                    case "bad-type": state["metadata"]!["matches"] = "0"; break;
                    case "bad-source-claim": state["metadata"]!["display"] = read["state"]!["metadata"]!["display"]!.DeepClone(); break;
                    case "bad-truncation": state["metadata"]!["truncated"] = true; break;
                    case "bad-path": state["input"]!["path"] = "../outside"; break;
                    case "bad-status": state["status"] = "error"; break;
                    case "bad-tool": other["tool"] = "bash"; break;
                    case "bad-identity": other["messageID"] = "msg_other"; break;
                }
                parts.Add(other);
            }
            if (scenario == "unrelated-only") { parts.Clear(); parts.Add(Other("grep", 1)); }
            var batch = new JsonArray(Assistant(f, "msg_tools", parent, 1, "tool-calls", parts),
                Assistant(f, "msg_reply", parent, 4, "stop", new JsonArray(new JsonObject { ["id"] = "prt_summary", ["sessionID"] = "ses_runtime", ["messageID"] = "msg_reply", ["type"] = "text", ["text"] = "Source-derived offline result; semantic quality untested." })));
            response.Content = Json(batch); return response;
        };
        return f;
    }
    [Theory]
    [InlineData("conflict-tail")] [InlineData("different-range")] [InlineData("bad-type")]
    [InlineData("bad-source-claim")] [InlineData("bad-truncation")] [InlineData("bad-path")]
    [InlineData("bad-status")] [InlineData("bad-tool")] [InlineData("bad-identity")] [InlineData("unrelated-only")]
    public async Task AmbiguousOrNonSourceObservationsCannotSupplyPositiveEvidence(string scenario)
    {
        using var f = await Fixture(scenario, "1.19.0"); using var chat = new Chat(); var reader = new BoundedSourceReader(f.F.Runtime.Root, f.F.Clock);
        var run = await f.Flow(sources: new VerifiedSourceMode(reader), reviewer: new NativeEvidenceReviewer(chat).ReviewAsync).RunAsync(f.Task);
        Assert.Empty(run.Outcome.ExternalResult!.SourceCandidates); Assert.Equal(ExternalExecutionStatus.Completed, run.Outcome.ExternalResult.Status);
        Assert.Equal(0, reader.ReadAttempts); Assert.Equal(0, chat.Calls); Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, run.Outcome.Review!.Status);
        Assert.Equal(HumanGateStatus.NeedsHumanReview, run.Outcome.HumanGate); Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f));
    }
    [Fact]
    public async Task TwoNewExcerptsStillRequireBothNativeObservations()
    {
        using var f = await Fixture("two-excerpt", "1.19.0"); using var chat = new Chat { Partial = true }; var reader = new BoundedSourceReader(f.F.Runtime.Root, f.F.Clock);
        var run = await f.Flow(sources: new VerifiedSourceMode(reader), reviewer: new NativeEvidenceReviewer(chat).ReviewAsync).RunAsync(f.Task);
        Assert.Equal(2, run.Outcome.ExternalResult!.SourceCandidates.Length); Assert.Equal(2, run.Outcome.Review!.Sources!.Admitted.Length);
        Assert.Equal(4, reader.ReadAttempts); Assert.Equal(2, chat.Calls); Assert.Contains("Example.cs:1:", chat.ToolEvidence); Assert.DoesNotContain("Second.cs:", chat.ToolEvidence);
        Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, run.Outcome.Review.Status); Assert.Equal(HumanGateStatus.NeedsHumanReview, run.Outcome.HumanGate);
        Assert.Contains(run.Outcome.Limitations, l => l.Contains("excerpt", StringComparison.Ordinal)); Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f));
    }
    [Fact]
    public async Task FreshInspectRetainsExcerptDisclosureWithoutPersistedCandidateFallback()
    {
        using var f = await Fixture("middle", "1.19.0"); using var chat = new Chat();
        var first = await f.Flow(sources: new VerifiedSourceMode(new(f.F.Runtime.Root, f.F.Clock)), reviewer: new NativeEvidenceReviewer(chat).ReviewAsync).RunAsync(f.Task);
        Assert.Equal(HumanGateStatus.ReadyForHumanReview, first.Outcome.HumanGate); f.Fake.Requests.Clear();
        using var next = new Chat();
        var second = await f.Flow(mode: ExternalAdmissionMode.Inspect, sources: new VerifiedSourceMode(new(f.F.Runtime.Root, f.F.Clock)), reviewer: new NativeEvidenceReviewer(next).ReviewAsync).RunAsync(f.Task);
        Assert.Equal(first.Outcome.ExternalResult!.Handle, second.Outcome.ExternalResult!.Handle); Assert.Equal(HumanGateStatus.ReadyForHumanReview, second.Outcome.HumanGate);
        Assert.Contains(second.Outcome.Limitations, l => l.Contains("provider-truncated=true", StringComparison.Ordinal)); Assert.Equal(new[] { 0, 0, 9, 0 }, Counts(f));
        Assert.Empty(JsonSerializer.Deserialize<ExternalTaskResult>(second.Outcome.ExternalResult.ToJson(), ExternalTaskResult.JsonOptions)!.SourceCandidates);
        f.Fake.Requests.Clear(); f.ResponseTransform = null;
        var missing = await f.Flow(mode: ExternalAdmissionMode.Inspect, sources: new VerifiedSourceMode(new(f.F.Runtime.Root, f.F.Clock)), reviewer: new NativeEvidenceReviewer(next).ReviewAsync).RunAsync(f.Task);
        Assert.Empty(missing.Outcome.ExternalResult!.SourceCandidates); Assert.Equal(EvidenceAssessmentStatus.InsufficientEvidence, missing.Outcome.Review!.Status); Assert.Equal(new[] { 0, 0, 9, 0 }, Counts(f));
    }
    static StringContent Json(object obj) => new(JsonSerializer.Serialize(obj), Encoding.UTF8, "application/json");
    static JsonNode Assistant(SyntheticProtectedHandoff f, string id, string parent, int created, string finish, JsonArray parts) => JsonSerializer.SerializeToNode(new
    { info = new { id, sessionID = "ses_runtime", role = "assistant", parentID = parent, time = new { created, completed = created + 1 }, finish,
        modelID = "scripted", providerID = "synthetic", mode = "build", agent = "maf_readonly", path = new { cwd = f.F.Runtime.Root, root = f.F.Runtime.Root }, cost = 0,
        tokens = new { input = 0, output = 0, reasoning = 0, cache = new { read = 0, write = 0 } } }, parts })!;
    static JsonNode Read(SyntheticProtectedHandoff f, string file, string[] lines, int start, int limit, int index)
    {
        // Source-derived ReadTool formatting: preview first20, explicit display range, line clipping.
        var raw = lines.Skip(start - 1).Take(limit).Select(l => l.Length > 2000 ? l[..2000] + "... (line truncated to 2000 chars)" : l).ToArray();
        var end = start + raw.Length - 1; var truncated = end < lines.Length; var path = Path.Combine(f.F.Runtime.Root, file);
        return JsonSerializer.SerializeToNode(new { id = "prt_read" + index, sessionID = "ses_runtime", messageID = "msg_tools", type = "tool", callID = "call_read" + index, tool = "read",
            state = new { status = "completed", input = new { filePath = path, offset = start, limit }, title = file, time = new { start = 1, end = 2 },
                output = $"<path>{path}</path>\n<type>file</type>\n<content>\n\n" + string.Join('\n', raw.Select((line, i) => $"{start + i}: {line}"))
                    + (truncated ? $"\n\n(Showing lines {start}-{end} of {lines.Length}. Use offset={end + 1} to continue.)" : $"\n\n(End of file - total {lines.Length} lines)") + "\n</content>",
                metadata = new { preview = string.Join('\n', raw.Take(20)), truncated, loaded = Array.Empty<string>(),
                    display = new { type = "file", path, text = string.Join('\n', raw), lineStart = start, lineEnd = end, totalLines = lines.Length, truncated } } } })!;
    }
    static JsonNode Other(string tool, int index) => JsonSerializer.SerializeToNode(new { id = "prt_other" + index, sessionID = "ses_runtime", messageID = "msg_tools", type = "tool", callID = "call_other" + index, tool,
        state = new { status = "completed", input = new { pattern = tool == "grep" ? "line" : "*.cs" }, title = tool == "glob" ? "" : "line", output = tool == "grep" ? "No files found" : "Example.cs",
            time = new { start = 1, end = 2 }, metadata = tool == "grep" ? new Dictionary<string, object> { ["matches"] = 0, ["truncated"] = false } : new Dictionary<string, object> { ["count"] = 1, ["truncated"] = false } } })!;
    static int[] Counts(SyntheticProtectedHandoff f) => [f.Fake.Requests.Count(r => r == "POST /session"), f.Fake.Requests.Count(r => r.EndsWith("/prompt_async")), f.Fake.Requests.Count(r => r.StartsWith("GET ")), f.Fake.Requests.Count(r => r.EndsWith("/abort"))];

    [Theory, MemberData(nameof(Scenarios))]
    public async Task SourceGroundedCoverage(string scenario, string version)
    {
        using var f = await Fixture(scenario, version); using var chat = new Chat(); var reader = new BoundedSourceReader(f.F.Runtime.Root, f.F.Clock);
        var run = await f.Flow(sources: new VerifiedSourceMode(reader), reviewer: new NativeEvidenceReviewer(chat).ReviewAsync,
            handoff: scenario == "changed" ? () => File.WriteAllText(Path.Combine(f.F.Runtime.Root, "Example.cs"), "// changed") : null).RunAsync(f.Task);
        var result = run.Outcome.ExternalResult!; var expected = scenario is "default2000" or "oversized" or "long-line" or "conflict" ? 0 : scenario == "two" ? 2 : 1;
        output.WriteLine($"{scenario}/{version}: extracted={result.SourceCandidates.Length}; verified={run.Outcome.Review!.Sources!.Admitted.Length}; modelCalls={chat.Calls}; status={run.Outcome.Review.Status}; effects={string.Join('/', Counts(f))}");
        Assert.Equal(expected, result.SourceCandidates.Length);
        Assert.Equal(ExternalExecutionStatus.Completed, result.Status); Assert.Equal(new[] { 1, 1, 9, 0 }, Counts(f));
        Assert.Single(run.Events.OfType<WorkflowOutputEvent>()); Assert.Equal(new[] { "PCA", "ExternalDelegation", "EvidenceReviewer", "HumanGate" }, run.Events.OfType<ExecutorInvokedEvent>().Select(e => e.ExecutorId));
        var success = expected > 0 && scenario != "changed";
        Assert.Equal(success ? HumanGateStatus.ReadyForHumanReview : HumanGateStatus.NeedsHumanReview, run.Outcome.HumanGate);
        Assert.Equal(success ? 2 : 0, chat.Calls);
        if (success)
        {
            Assert.Equal(expected, run.Outcome.Review.Sources.Admitted.Length); Assert.Equal(expected * 2, reader.ReadAttempts);
            var candidate = result.SourceCandidates[0]; var start = scenario == "middle" ? 30 : 1;
            var count = scenario is "over20" or "default100" ? 20 : scenario is "middle" or "truncated" ? 10 : scenario == "anchor-budget" ? 8 : 5;
            Assert.Equal(start, candidate.StartLine); Assert.Equal(start + count - 1, candidate.EndLine);
            Assert.Equal(candidate.ExpectedAnchor, run.Outcome.Review.Sources.Admitted[0].Content); Assert.InRange(candidate.ExpectedAnchor!.Length, 1, 512);
            Assert.Contains($"Example.cs:{start}:", chat.ToolEvidence);
            Assert.All(result.SourceCandidates, c => { Assert.Equal(f.Task.TaskId, c.Binding.TaskId); Assert.Equal("ses_runtime", c.Binding.SessionId); });
            if (scenario is "over20" or "default100" or "middle" or "truncated" or "anchor-budget") Assert.Contains(result.Limitations, l => l.Contains("excerpt", StringComparison.OrdinalIgnoreCase));
            if (scenario is "middle" or "truncated") Assert.Contains(result.Limitations, l => l.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        }
    }
    sealed class Chat : IChatClient
    {
        public int Calls; public bool Partial; public string ToolEvidence = "";
        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++; var input = messages.ToArray(); var user = input.Last(m => m.Role == ChatRole.User).Text!;
            using var json = JsonDocument.Parse(user["Untrusted task data: ".Length..]); var sources = json.RootElement.GetProperty("Sources").EnumerateArray().ToArray();
            var results = input.Where(m => m.Role == ChatRole.Tool).SelectMany(m => m.Contents).OfType<FunctionResultContent>().ToArray();
            if (results.Length == 0) return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, sources.Take(Partial ? 1 : sources.Length).Select((s,i) => (AIContent)new FunctionCallContent("read"+i,"ReadEvidence",
                new Dictionary<string, object?> { ["reference"] = s.GetProperty("Reference").GetString(), ["startLine"] = s.GetProperty("StartLine").GetInt32(), ["endLine"] = s.GetProperty("EndLine").GetInt32() })).ToArray())));
            ToolEvidence = string.Join('\n', results.Select(r => r.Result?.ToString()));
            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, JsonSerializer.Serialize(new { status = "SufficientEvidence", summary = "Bounded source observations only.", evidenceReferences = sources.Select(s => s.GetProperty("Reference").GetString()).ToArray(), limitations = Array.Empty<string>() }))));
        }
        public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default) { await Task.CompletedTask; yield break; }
        public object? GetService(Type type, object? key = null) => null;
        public void Dispose() { }
    }
    public void Dispose() { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if (Directory.Exists(root)) Directory.Delete(root,true); }
}
