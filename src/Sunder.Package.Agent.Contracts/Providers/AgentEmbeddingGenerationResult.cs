namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Contains the vector generated for one embedding input.
/// </summary>
/// <remarks>
/// Providers must return a value collection that remains stable after the operation completes;
/// callers treat it as read-only and may persist it beyond the provider call.
/// </remarks>
/// <param name="ModelId">The provider-resolvable model identity that generated the vector.</param>
/// <param name="Values">The ordered floating-point components of the embedding vector.</param>
public sealed record AgentEmbeddingGenerationResult(
    string ModelId,
    IReadOnlyList<float> Values)
{
    /// <summary>Gets the vector length reported by <see cref="Values" />.</summary>
    public int Dimensions => Values.Count;
}
