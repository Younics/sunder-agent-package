using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Identifies the author or provider channel represented by a transcript turn.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentMessageRole
{
    /// <summary>Host-authorized system guidance. Untrusted recalled context must not be assigned this role.</summary>
    [JsonStringEnumMemberName("system")] System = 0,

    /// <summary>User-authored input, including messages that carry attachment items.</summary>
    [JsonStringEnumMemberName("user")] User = 1,

    /// <summary>Assistant/model output, including streamed text and tool requests.</summary>
    [JsonStringEnumMemberName("assistant")] Assistant = 2,

    /// <summary>Tool-produced output correlated to an assistant tool call.</summary>
    [JsonStringEnumMemberName("tool")] Tool = 3,
}
