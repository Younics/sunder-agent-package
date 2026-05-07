namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProcessCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null);
