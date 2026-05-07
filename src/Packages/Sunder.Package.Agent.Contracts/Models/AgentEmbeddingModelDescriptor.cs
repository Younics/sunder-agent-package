namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentEmbeddingModelDescriptor(
    string ModelId,
    string DisplayName,
    int? Dimensions = null,
    bool IsRecommended = false);
