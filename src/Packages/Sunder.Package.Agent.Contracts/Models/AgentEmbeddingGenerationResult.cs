namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentEmbeddingGenerationResult(
    string ModelId,
    IReadOnlyList<float> Values)
{
    public int Dimensions => Values.Count;
}
