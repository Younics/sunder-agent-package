using System.Text.Json.Serialization;

namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes the durable dispatch state of a provider-requested tool invocation.
/// </summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentToolExecutionStatus
{
    /// <summary>The invocation is durable but has not entered its tool source.</summary>
    [JsonStringEnumMemberName("prepared")] Prepared = 0,

    /// <summary>The invocation crossed the durable dispatch boundary.</summary>
    [JsonStringEnumMemberName("started")] Started = 1,

    /// <summary>The invocation completed successfully or durably suspended child work.</summary>
    [JsonStringEnumMemberName("completed")] Completed = 2,

    /// <summary>The invocation did not dispatch or returned a known failure.</summary>
    [JsonStringEnumMemberName("failed")] Failed = 3,

    /// <summary>The invocation dispatched, but its external effects could not be determined.</summary>
    [JsonStringEnumMemberName("ambiguous")] Ambiguous = 4,
}
