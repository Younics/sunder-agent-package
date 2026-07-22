namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Allows a chat provider to select a model for short, provider-backed utility tasks such as title generation.
/// </summary>
/// <remarks>
/// This optional contract does not establish provider readiness. Callers check readiness separately
/// and create a normal chat client for the returned model.
/// </remarks>
public interface IAgentUtilityModelProvider
{
    /// <summary>Resolves the current utility model selection for this provider.</summary>
    /// <param name="cancellationToken">A token that cancels configuration or model discovery work.</param>
    /// <returns>A model identifier accepted by the same provider, or <see langword="null" /> when no utility model is available.</returns>
    ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default);
}
