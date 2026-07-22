namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the workspace roots and default directory in an execution target's own path namespace.
/// </summary>
/// <remarks>
/// This descriptor is intended for discovery and prompt construction. It is not an access grant: targets must still enforce lexical and
/// physical containment, including symbolic-link or mount traversal, when an operation is executed. The path collection is a snapshot and
/// should contain canonical absolute roots in the same order as the workspace's configured host paths when a stable correspondence exists.
/// </remarks>
/// <param name="DisplayName">The user-facing name of the execution environment whose paths are described.</param>
/// <param name="WorkspacePaths">
/// Canonical target-visible workspace roots. These may differ from host paths, for example when host directories are mounted into a container.
/// </param>
/// <param name="DefaultWorkingDirectory">
/// The target-visible directory used when a request omits a working directory, or <see langword="null"/> when the target has no default.
/// When present, it should be contained by one of <paramref name="WorkspacePaths"/>.
/// </param>
/// <param name="PathStyleDescription">
/// Optional user- and model-facing guidance about path syntax. It must not contain credentials or other sensitive host details.
/// </param>
public sealed record AgentExecutionScopeDescriptor(
    string DisplayName,
    IReadOnlyList<string> WorkspacePaths,
    string? DefaultWorkingDirectory = null,
    string? PathStyleDescription = null);
