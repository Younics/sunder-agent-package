namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolSourceItem(
    string Title,
    string Url,
    string? Snippet = null);
