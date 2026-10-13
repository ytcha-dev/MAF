using MAF.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
namespace MAF.Agents.Comprehension;
public sealed class ComprehensionAgentFactory(IChatClient chatClient, ProjectTools projectTools) : IAgentFactory
{
    public AIAgent Create() => new ChatClientAgent(chatClient,
        name: "ProjectComprehensionAgent", instructions: "以繁體中文回答。你是協助理解本機專案的 PCA。請先讀取工具證據再回答專案問題。",
        tools: [AIFunctionFactory.Create(projectTools.ListFiles), AIFunctionFactory.Create(projectTools.ReadFile),
            AIFunctionFactory.Create(projectTools.SearchCode), AIFunctionFactory.Create(projectTools.GetGitDiff),
            AIFunctionFactory.Create(projectTools.GetGitStatus)]);
}
