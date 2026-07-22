namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requires a matching profile capability assignment before a tool can be advertised.
/// </summary>
/// <remarks>
/// When present on a descriptor, this requirement is evaluated instead of its normal individual or
/// group selection rule. Null source or capability identifiers act as wildcards within the required kind.
/// </remarks>
/// <param name="CapabilityKind">The required selectable-capability kind.</param>
/// <param name="SourceId">An optional source identifier that the assignment must match.</param>
/// <param name="CapabilityId">An optional capability identifier that the assignment must match.</param>
public sealed record AgentToolActivationRequirement(
    string CapabilityKind,
    string? SourceId = null,
    string? CapabilityId = null);
