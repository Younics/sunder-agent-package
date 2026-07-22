namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the stable identity and supported authentication mechanisms of an embedding provider.
/// </summary>
/// <param name="ProviderId">The stable, case-insensitive identifier persisted in model bindings.</param>
/// <param name="DisplayName">The provider name shown to users.</param>
/// <param name="SupportedAuthModes">The authentication flows the provider can use; this list contains no credential material.</param>
public sealed record AgentEmbeddingProviderDescriptor(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<AgentAuthMode> SupportedAuthModes)
{
    /// <summary>
    /// Gets the Sunder package that owns the provider contribution, or <see langword="null" /> when ownership is not package-scoped.
    /// </summary>
    public string? PackageId { get; init; }
}
