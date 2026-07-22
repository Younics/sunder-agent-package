namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes whether a provider can accept requests with its current configuration.
/// </summary>
/// <remarks>
/// Readiness is a point-in-time observation. A ready provider can still fail when a request is
/// made, and callers should not cache the status as an authorization or availability guarantee.
/// </remarks>
public enum AgentProviderReadinessStatus
{
    /// <summary>The provider has the configuration and credentials needed to attempt a request.</summary>
    Ready = 0,

    /// <summary>User configuration, authorization, a secret, or a local model selection is required.</summary>
    NeedsConfiguration = 1,

    /// <summary>The provider is configured but an operational check failed.</summary>
    Failed = 2,
}
