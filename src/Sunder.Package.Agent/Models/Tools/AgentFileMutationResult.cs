namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports the outcome of a file write or delete attempted by an execution target.
/// </summary>
/// <param name="Path">The target-resolved path, or the requested path when resolution failed.</param>
/// <param name="Summary">A concise, user-facing outcome that may be persisted and returned to the model.</param>
/// <param name="IsError">Whether the requested mutation did not complete successfully.</param>
/// <param name="ErrorCode">An optional stable machine-readable failure code.</param>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentFileMutationResult(
    string Path,
    string Summary,
    bool IsError = false,
    string? ErrorCode = null)
{
    /// <summary>Gets a transient receipt bound to the actual mutated object or deletion state, when requested by the caller.</summary>
    /// <remarks>The receipt can carry process-local authority and must not be persisted. A receipt on an
    /// error result is not evidence that the requested complete content was written.</remarks>
    [System.Text.Json.Serialization.JsonIgnore]
    [Sunder.Sdk.Services.SunderServiceMember]
    public AgentResolvedResource? PostMutationResource { get; init; }
}
