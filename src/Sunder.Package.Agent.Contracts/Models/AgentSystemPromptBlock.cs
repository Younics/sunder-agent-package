namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentSystemPromptBlock(
    string BlockId,
    string Title,
    string Content,
    int Priority = 0,
    bool Required = false,
    int? MaxChars = null,
    string? SourceId = null,
    IReadOnlyDictionary<string, string>? Metadata = null);
