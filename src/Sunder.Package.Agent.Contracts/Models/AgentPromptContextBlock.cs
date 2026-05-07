namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPromptContextBlock(
    string Title,
    string Content,
    int Priority = 0,
    string? SourceId = null);
