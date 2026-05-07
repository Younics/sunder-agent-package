namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPromptContextContribution(
    IReadOnlyList<AgentPromptContextBlock> Blocks);
