namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceRecord(
    string WorkspaceId,
    string DisplayName,
    string? Description,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    IReadOnlyList<AgentWorkspacePathRecord>? Paths = null,
    IReadOnlyList<AgentWorkspaceDocumentRecord>? Documents = null)
{
    public IReadOnlyList<AgentWorkspacePathRecord> Paths { get; init; } = Paths ?? [];

    public IReadOnlyList<AgentWorkspaceDocumentRecord> Documents { get; init; } = Documents ?? [];
}

public sealed record AgentWorkspacePathRecord(
    string PathId,
    string WorkspaceId,
    string HostPath,
    bool IsDefault,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);

public sealed record AgentWorkspaceDocumentRecord(
    string DocumentId,
    string WorkspaceId,
    string FilePath,
    int SortOrder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc);
