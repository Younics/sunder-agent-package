namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentShellCommandRequest(
    string Command,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null);
