namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes a chat provider's stable identity and provider-wide transport capabilities.
/// </summary>
/// <remarks>
/// Authentication support advertises available flows only; readiness determines whether the
/// selected flow currently has the required authorization or secret. Model-specific behavior is
/// reported by <see cref="AgentProviderRunCapabilities" />.
/// </remarks>
/// <param name="ProviderId">The stable, case-insensitive identifier persisted in model bindings.</param>
/// <param name="DisplayName">The provider name shown to users.</param>
/// <param name="SupportedAuthModes">The authentication flows the provider can use; this list contains no credential material.</param>
/// <param name="SupportsStreaming">Whether chat clients can deliver incremental response updates.</param>
/// <param name="SupportsInterruptibleRuns">Whether clients observe cancellation while a request or response stream is active; remote work or charges may still continue.</param>
public sealed record AgentProviderDescriptor(
    string ProviderId,
    string DisplayName,
    IReadOnlyList<AgentAuthMode> SupportedAuthModes,
    bool SupportsStreaming,
    bool SupportsInterruptibleRuns)
{
    /// <summary>
    /// Gets the Sunder package that owns the provider contribution, or <see langword="null" /> when ownership is not package-scoped.
    /// </summary>
    public string? PackageId { get; init; }
}
