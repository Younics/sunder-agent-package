namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentEmbeddingProviderReadiness(
    string ProviderId,
    AgentProviderReadinessStatus Status,
    string Message);
