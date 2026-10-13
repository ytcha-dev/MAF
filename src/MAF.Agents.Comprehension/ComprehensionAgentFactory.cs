using MAF.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
namespace MAF.Agents.Comprehension;
public sealed class ComprehensionAgentFactory(IChatClient chatClient) : IAgentFactory
{
    public AIAgent Create() => new ChatClientAgent(chatClient,
        name: "ProjectComprehensionAgent", instructions: "以繁體中文回答。你是協助理解本機專案的 PCA。", tools: []);
}
