namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requests execution of an untrusted command by the shell selected for an execution target.
/// </summary>
/// <remarks>
/// Shell execution is mutating and requires permission even when a particular command appears
/// read-only. The target resolves the working directory against its configured scope. Cancellation
/// or timeout requests process termination but cannot roll back filesystem, network, or child-process
/// effects that occurred before termination.
/// </remarks>
/// <param name="Command">The complete command interpreted using the selected shell's syntax.</param>
/// <param name="WorkingDirectory">An optional workspace-relative or target-resolvable working directory.</param>
/// <param name="TimeoutSeconds">An optional positive timeout; <see langword="null" /> uses the target's bounded default.</param>
public sealed record AgentShellCommandRequest(
    string Command,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null);
