namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Identifies an Agent extension for discovery and presentation.</summary>
public sealed record AgentServiceIdentity(string Id, string DisplayName);

/// <summary>Describes a source's stable tool identity and category.</summary>
public sealed record AgentToolSourceDescriptor(string SourceId, string DisplayName, string SourceKind);

/// <summary>Describes the actions and boundaries offered by a permission surface.</summary>
public sealed record AgentPermissionSurfaceDescriptor(
    [property: Sunder.Sdk.Services.SunderValueConstraints(MinLength = 1)] string SurfaceId, string DisplayName, IReadOnlyList<AgentPermissionActionDescriptor> Actions);

/// <summary>Describes the model capabilities consumed by an extension.</summary>
public sealed record AgentProfileCapabilityConsumerSnapshot(
    string ConsumerId, string DisplayName, IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> Capabilities);

/// <summary>Describes prompt contribution and optional acknowledgement support.</summary>
public sealed record AgentPromptContextContributorDescriptor(
    string ContributorId, string DisplayName, string? AcknowledgmentSinkId = null);

/// <summary>A monotonically increasing capability-catalog invalidation revision.</summary>
public sealed record AgentCapabilityCatalogChange(long Revision);
