using MAF.Core.Tools;
using MAF.Agents.Comprehension;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;

namespace MAF.Tests;

public sealed class AgentTests
{
    [Fact]
    public async Task OfficialAgentRunsAndKeepsSession()
    {
        using var client = new RecordingClient();
        var agent = new ComprehensionAgentFactory(client, new ProjectTools(AppContext.BaseDirectory)).Create();
        Assert.IsType<ChatClientAgent>(agent);
        var session = await agent.CreateSessionAsync();
        Assert.Contains("fixture reply", (await agent.RunAsync("first", session)).Text);
        await agent.RunAsync("second", session);
        Assert.Contains(client.LastMessages, m => m.Text == "first");
        Assert.Contains(client.LastMessages, m => m.Text == "second");
    }
}

internal sealed class RecordingClient : IChatClient
{
    public List<ChatMessage> LastMessages { get; private set; } = [];
    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
    {
        LastMessages = messages.ToList();
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, "fixture reply")));
    }
    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    { await Task.CompletedTask; yield break; }
    public object? GetService(Type serviceType, object? serviceKey = null) => null;
    public void Dispose() { }
}
