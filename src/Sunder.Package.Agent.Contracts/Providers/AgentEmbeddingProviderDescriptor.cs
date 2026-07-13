namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentEmbeddingProviderDescriptor(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<AgentAuthMode> SupportedAuthModes)
{
    public string? PackageId { get; init; }
}
