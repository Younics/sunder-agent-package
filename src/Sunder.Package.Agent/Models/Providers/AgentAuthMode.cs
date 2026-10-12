using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies supported provider authorization flows without carrying credential material.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentAuthMode
{
    /// <summary>An API key is retrieved by the provider from host-managed secret storage.</summary>
    [JsonStringEnumMemberName("api-key")]
    ApiKey = 0,

    /// <summary>A provider-managed connected-account session authorizes chat requests without exposing its tokens to callers.</summary>
    [JsonStringEnumMemberName("codex-connected")]
    CodexConnected = 1,
}
