using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Supplies native <see cref="Microsoft.Extensions.AI.AIFunctionDeclaration" /> instances when a source has richer schema metadata than a descriptor can reconstruct.
/// </summary>
/// <remarks>
/// The descriptor remains authoritative for selection, permissions, mutation, and concurrency. Its
/// tool identity, and its schema when supplied, must agree with the declaration sent to the model.
/// Invocation continues to flow through <see cref="IAgentToolSource.ExecuteAsync" />.
/// </remarks>
public interface IAgentNativeToolSource : IAgentToolSource
{
    /// <summary>Lists ready-candidate tool descriptors paired with their model-facing declarations.</summary>
    /// <param name="context">The current catalog context used to select source-owned tools.</param>
    /// <param name="cancellationToken">A token that cancels discovery, connection, or schema loading.</param>
    /// <returns>A stable read-only snapshot whose declarations remain valid for the provider run.</returns>
    ValueTask<IReadOnlyList<AgentRuntimeTool>> ListRuntimeToolsAsync(
        AgentToolSourceContext context,
        CancellationToken cancellationToken = default);
}
