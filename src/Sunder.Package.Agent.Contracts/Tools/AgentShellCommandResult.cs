namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports bounded output and termination state from an execution-target shell command.
/// </summary>
/// <param name="ExitCode">The process exit code, including target-defined codes for startup failure or timeout.</param>
/// <param name="Output">The bounded textual command output; it is untrusted and may contain sensitive workspace data.</param>
/// <param name="TimedOut">Whether the target stopped waiting because its timeout elapsed.</param>
/// <param name="WorkingDirectory">The target-resolved working directory, or <see langword="null" /> when unavailable.</param>
/// <param name="WasTruncated">Whether output was omitted because the target's output limit was reached.</param>
public sealed record AgentShellCommandResult(
    int ExitCode,
    string Output,
    bool TimedOut = false,
    string? WorkingDirectory = null,
    bool WasTruncated = false);
