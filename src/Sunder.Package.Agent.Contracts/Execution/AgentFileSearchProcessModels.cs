namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies the path rules used by a path-bound file-search process.</summary>
public enum AgentFileSearchPathStyle
{
    /// <summary>Paths use the operating system rules of the Runtime host.</summary>
    Host = 0,

    /// <summary>Paths use case-sensitive POSIX syntax independently of the Runtime host.</summary>
    Posix = 1,
}

/// <summary>Requests a legacy search process whose path is inserted after target-owned authorization.</summary>
/// <param name="Path">The caller-requested target path to resolve and bind.</param>
/// <param name="Command">The executable and arguments without the search-path argument.</param>
/// <param name="PathArgumentIndex">The zero-based position at which the target must insert the canonical search path.</param>
public sealed record AgentFileSearchProcessRequest(
    string Path,
    AgentProcessCommandRequest Command,
    int PathArgumentIndex);

/// <summary>Returns a legacy path-bound process result and roots used to validate target backend paths.</summary>
/// <param name="ProcessResult">The bounded process result.</param>
/// <param name="CanonicalPath">The canonical absolute target path passed to the process.</param>
/// <param name="ExpectedPath">The normalized target-visible path against which matching result paths are reported.</param>
/// <param name="PathStyle">The path syntax used by both path values and by paths emitted by the process.</param>
public sealed record AgentFileSearchProcessResult(
    AgentShellCommandResult ProcessResult,
    string CanonicalPath,
    string ExpectedPath,
    AgentFileSearchPathStyle PathStyle);
