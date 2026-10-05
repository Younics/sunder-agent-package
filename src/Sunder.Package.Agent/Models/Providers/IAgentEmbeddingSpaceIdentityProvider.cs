namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Supplies the non-secret provider configuration identity that defines an embedding vector space.
/// </summary>
/// <remarks>
/// Implement this optional contract when endpoint or provider configuration can change the vectors
/// produced for the same model id. The identity must be stable, must change whenever that vector
/// space changes, and must never contain credentials or other secrets.
/// </remarks>
public interface IAgentEmbeddingSpaceIdentityProvider
{
    /// <summary>Gets the current non-secret embedding-space identity for a provider-owned model.</summary>
    ValueTask<string> GetEmbeddingSpaceIdentityAsync(
        string modelId,
        CancellationToken cancellationToken = default);
}
