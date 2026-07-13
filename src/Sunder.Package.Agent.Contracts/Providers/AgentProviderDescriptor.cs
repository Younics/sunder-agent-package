namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentProviderDescriptor(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<AgentAuthMode> SupportedAuthModes,
    bool SupportsStreaming,
    bool SupportsInterruptibleRuns)
{
    public string? PackageId { get; init; }
}
