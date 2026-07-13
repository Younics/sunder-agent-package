namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceBindingRecord(
    string BindingId,
    string WorkspaceId,
    string ExtensionPointId,
    string ContributionId,
    string Role,
    bool IsEnabled,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
