namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requests execution of a program with structured argument boundaries instead of a caller-authored shell command.
/// </summary>
/// <remarks>
/// The execution target owns the child process, standard streams, timeout handling, and cleanup; no process handle or stream is transferred
/// to the caller. A target may still use an internal shell or remote transport, so callers must treat the file name and arguments as
/// security-sensitive input and obtain execution permission before submitting the request.
/// </remarks>
/// <param name="FileName">
/// The executable name or target-visible executable path. Bare names may be resolved through the target's effective <c>PATH</c>.
/// </param>
/// <param name="Arguments">The ordered arguments to pass to the executable, without caller-added shell quoting.</param>
/// <param name="WorkingDirectory">
/// An optional target-visible working directory. Relative paths are resolved by the target; <see langword="null"/> selects its configured default.
/// </param>
/// <param name="TimeoutSeconds">
/// An optional positive timeout. <see langword="null"/> selects the target's configured default, and targets may reject values outside their limits.
/// </param>
public sealed record AgentProcessCommandRequest(
    string FileName,
    IReadOnlyList<string> Arguments,
    string? WorkingDirectory = null,
    int? TimeoutSeconds = null);
