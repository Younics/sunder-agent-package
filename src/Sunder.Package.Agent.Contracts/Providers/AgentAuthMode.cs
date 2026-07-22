namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies supported provider authorization flows without carrying credential material.
/// </summary>
public enum AgentAuthMode
{
    /// <summary>An API key is retrieved by the provider from host-managed secret storage.</summary>
    ApiKey = 0,

    /// <summary>A provider-managed connected-account session authorizes chat requests without exposing its tokens to callers.</summary>
    CodexConnected = 1,
}
