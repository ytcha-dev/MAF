using MAF.Agents.Comprehension;
using Microsoft.Extensions.AI;

namespace MAF.Tests;

public sealed class FunctionCallingTests
{
    [Fact]
    public async Task MafExecutesRegisteredToolsAndReturnsSanitizedEvidenceToClient()
    {
        var root = Path.Combine(Path.GetTempPath(), "pca-call-" + Guid.NewGuid()); Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "Program.cs"), "// entry\nvar apiKey = \"private-test-value\";");
            var tools = new ProjectTools(root);
            using var client = new ToolCallingClient();
            var agent = new ComprehensionAgentFactory(client, tools).Create();
            var response = await agent.RunAsync("分析專案入口");
            Assert.Contains("evidence received", response.Text);
            Assert.Equal(2, tools.SuccessfulCalls);
            Assert.Contains("Program.cs:1:", client.ToolEvidence);
            Assert.DoesNotContain("private-test-value", client.ToolEvidence);
            Assert.Equal(new[] { "GetGitDiff", "GetGitStatus", "ListFiles", "ReadFile", "SearchCode" }, client.ToolNames.Order());
        }
        finally { Directory.Delete(root, true); }
    }
}

internal sealed class ToolCallingClient : IChatClient
{
    private int calls;
    public string ToolEvidence { get; private set; } = "";
    public string[] ToolNames { get; private set; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        ToolNames = options!.Tools!.OfType<AIFunction>().Select(f => f.Name).ToArray();
        ChatMessage response;
        if (calls++ == 0)
            response = new(ChatRole.Assistant, [new FunctionCallContent("list-1", "ListFiles", new Dictionary<string, object?>())]);
        else if (calls == 2)
            response = new(ChatRole.Assistant, [new FunctionCallContent("read-1", "ReadFile", new Dictionary<string, object?> { ["path"] = "Program.cs", ["startLine"] = 1, ["maxLines"] = 10 })]);
        else
        {
            ToolEvidence = string.Join('\n', messages.SelectMany(m => m.Contents).OfType<FunctionResultContent>().Select(c => c.Result?.ToString()));
            response = new(ChatRole.Assistant, "evidence received");
        }
        return Task.FromResult(new ChatResponse(response));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await Task.CompletedTask; yield break; }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
