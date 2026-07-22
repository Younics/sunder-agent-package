using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

/// <summary>
/// Supplies embedding model discovery and vector generation for one provider identity.
/// </summary>
/// <remarks>
/// Implementations resolve credentials internally and must not expose them in metadata, readiness
/// messages, results, or exceptions. Returned catalogs and vectors must remain stable after each
/// operation. Caller-requested cancellation is propagated rather than converted to a missing result.
/// </remarks>
public interface IAgentEmbeddingProvider
{
    /// <summary>Gets immutable provider identity and supported authentication metadata.</summary>
    AgentEmbeddingProviderDescriptor Descriptor { get; }

    /// <summary>Lists embedding models currently selectable through this provider.</summary>
    /// <param name="cancellationToken">A token that cancels configuration or remote catalog discovery.</param>
    /// <returns>A stable read-only snapshot whose model identifiers are unique within the provider.</returns>
    ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);

    /// <summary>Checks whether the provider has the configuration and authorization required to generate embeddings.</summary>
    /// <param name="cancellationToken">A token that cancels configuration, secret, or connectivity checks.</param>
    /// <returns>A point-in-time readiness report for <see cref="Descriptor" />.</returns>
    ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);

    /// <summary>Generates one vector with the selected embedding model.</summary>
    /// <param name="modelId">The provider-resolvable model identifier.</param>
    /// <param name="text">The input text; implementations may return <see langword="null" /> for an empty or unsupported individual input.</param>
    /// <param name="cancellationToken">A token that cancels generation.</param>
    /// <returns>The generated vector, or <see langword="null" /> when no vector was produced for the input.</returns>
    ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        string modelId,
        string text,
        CancellationToken cancellationToken = default);

    /// <summary>Generates vectors for a batch while preserving input order.</summary>
    /// <remarks>
    /// The returned list has one position per input in <paramref name="texts" />. A null item means
    /// no vector was produced for that input; expected request-wide failures are reported by throwing.
    /// </remarks>
    /// <param name="modelId">The provider-resolvable model identifier used for every input.</param>
    /// <param name="texts">The ordered input texts.</param>
    /// <param name="cancellationToken">A token that cancels the complete batch request.</param>
    /// <returns>A stable read-only result list aligned with <paramref name="texts" />.</returns>
    ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        string modelId,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken = default);
}
