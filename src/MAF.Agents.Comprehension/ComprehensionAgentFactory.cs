using MAF.Core;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
namespace MAF.Agents.Comprehension;
public sealed class ComprehensionAgentFactory(IChatClient chatClient, ProjectTools projectTools) : IAgentFactory
{
    public AIAgent Create() => new ChatClientAgent(chatClient,
        name: "ProjectComprehensionAgent", instructions: ComprehensionInstructions.Text,
        tools: [AIFunctionFactory.Create(projectTools.ListFiles), AIFunctionFactory.Create(projectTools.ReadFile),
            AIFunctionFactory.Create(projectTools.SearchCode), AIFunctionFactory.Create(projectTools.GetGitDiff),
            AIFunctionFactory.Create(projectTools.GetGitStatus)]);
}
