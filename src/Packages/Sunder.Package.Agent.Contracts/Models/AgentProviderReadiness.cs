namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProviderReadiness(
    string ProviderId,
    AgentProviderReadinessStatus Status,
    string Message);
