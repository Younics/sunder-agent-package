namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes an embedding model exposed by an <see cref="Contracts.IAgentEmbeddingProvider" />.
/// </summary>
/// <param name="ModelId">The stable, provider-resolvable model identifier, normally qualified by provider.</param>
/// <param name="DisplayName">The model name shown to users.</param>
/// <param name="Dimensions">The fixed vector length, or <see langword="null" /> when unknown or configurable.</param>
/// <param name="IsRecommended">Whether the provider recommends this model as a catalog default; it does not force selection.</param>
public sealed record AgentEmbeddingModelDescriptor(
    string ModelId,
    string DisplayName,
    int? Dimensions = null,
    bool IsRecommended = false);
