using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Contracts.Models;

[SunderUnion(typeof(AgentRuntimeCatalogResetEvent), typeof(AgentSessionCatalogEvent), typeof(AgentTurnCatalogEvent), typeof(AgentProfileCatalogEvent))]
[SunderDiscriminator("kind")]
public abstract record AgentRuntimeCatalogEvent;

[SunderVariant("reset")]
public sealed record AgentRuntimeCatalogResetEvent(
    [property: SunderValueConstraints(Minimum = "0")] long Sequence) : AgentRuntimeCatalogEvent;

[SunderVariant("session")]
public sealed record AgentSessionCatalogEvent(
    [property: SunderValueConstraints(Minimum = "0")] long Sequence, Guid SessionId) : AgentRuntimeCatalogEvent;

[SunderVariant("turn")]
public sealed record AgentTurnCatalogEvent(
    [property: SunderValueConstraints(Minimum = "0")] long Sequence, Guid SessionId, AgentTurnRecord Turn) : AgentRuntimeCatalogEvent;

[SunderVariant("profile")]
public sealed record AgentProfileCatalogEvent(
    [property: SunderValueConstraints(Minimum = "0")] long Sequence, string ProfileId) : AgentRuntimeCatalogEvent;
