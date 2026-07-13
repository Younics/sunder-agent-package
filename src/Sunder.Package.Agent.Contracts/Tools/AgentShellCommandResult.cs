namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentShellCommandResult(
    int ExitCode,
    string Output,
    bool TimedOut = false,
    string? WorkingDirectory = null,
    bool WasTruncated = false);
