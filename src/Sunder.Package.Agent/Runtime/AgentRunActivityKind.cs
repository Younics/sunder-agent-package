using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies transient activity reported while an Agent run is active.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentRunActivityKind
{
    [JsonStringEnumMemberName("thinking")] Thinking = 0,
    [JsonStringEnumMemberName("reasoning")] Reasoning = 1,
    [JsonStringEnumMemberName("tool")] Tool = 2,
    [JsonStringEnumMemberName("processing")] Processing = 3,
    [JsonStringEnumMemberName("retrying")] Retrying = 4,
    [JsonStringEnumMemberName("responding")] Responding = 5,
}
