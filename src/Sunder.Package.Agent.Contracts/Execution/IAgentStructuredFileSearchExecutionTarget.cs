using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>Executes bounded structured Files searches under target-owned filesystem authority.</summary>
/// <remarks>
/// Implementations retain the root and traversed resource identities for the operation, reject links and target-specific boundary crossings,
/// and require an exact approved resource identity for an outside-scope path.
/// </remarks>
public interface IAgentStructuredFileSearchExecutionTarget : IAgentExecutionTarget
{
    /// <summary>Executes one structured search while retaining target-owned filesystem resources.</summary>
    ValueTask<AgentFileSearchResult> ExecuteFileSearchAsync(
        AgentExecutionTargetContext context,
        AgentFileSearchRequest request,
        CancellationToken cancellationToken = default);
}
