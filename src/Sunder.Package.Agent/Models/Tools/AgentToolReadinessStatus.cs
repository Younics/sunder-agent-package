namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes whether a tool may be advertised for the current source context.
/// </summary>
/// <remarks>Readiness is re-evaluated before execution and is not a permission grant.</remarks>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public enum AgentToolReadinessStatus
{
    /// <summary>The tool may be included in the executable catalog for the current context.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("ready")] Ready = 0,

    /// <summary>The tool must be omitted; its message explains missing configuration, unavailable resources, or an operational failure.</summary>
    [System.Text.Json.Serialization.JsonStringEnumMemberName("failed")] Failed = 3,
}
