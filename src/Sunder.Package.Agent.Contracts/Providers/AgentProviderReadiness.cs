namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports whether a chat provider can currently create and use a chat client.
/// </summary>
/// <param name="ProviderId">The stable identifier of the provider that produced the report.</param>
/// <param name="Status">The provider's point-in-time readiness state.</param>
/// <param name="Message">A user-facing explanation that must not contain API keys, tokens, or other secret values.</param>
public sealed record AgentProviderReadiness(
    string ProviderId,
    AgentProviderReadinessStatus Status,
    string Message);
