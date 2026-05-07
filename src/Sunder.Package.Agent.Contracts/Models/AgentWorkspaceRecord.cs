namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceRecord(
    string WorkspaceId,
    string DisplayName,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
