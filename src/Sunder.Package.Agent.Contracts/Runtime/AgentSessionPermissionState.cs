namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentSessionPermissionState(
    Guid SessionId,
    bool IsUnrestrictedModeEnabled);
