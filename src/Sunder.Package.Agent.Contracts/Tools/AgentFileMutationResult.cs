namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports the outcome of a file write or delete attempted by an execution target.
/// </summary>
/// <param name="Path">The target-resolved path, or the requested path when resolution failed.</param>
/// <param name="Summary">A concise, user-facing outcome that may be persisted and returned to the model.</param>
/// <param name="IsError">Whether the requested mutation did not complete successfully.</param>
/// <param name="ErrorCode">An optional stable machine-readable failure code.</param>
public sealed record AgentFileMutationResult(
    string Path,
    string Summary,
    bool IsError = false,
    string? ErrorCode = null)
{
    /// <summary>Gets a transient receipt for the exact state created by the mutation, when requested by the caller.</summary>
    /// <remarks>The receipt can carry process-local authority and must not be persisted.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    public AgentResolvedResource? PostMutationResource { get; init; }
}
