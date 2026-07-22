namespace Sunder.Package.Agent.Builder;

public sealed record BuilderProjectRecord(
    string Id,
    string DisplayName,
    string PackageId,
    string WorkspaceId,
    string ExecutionProjectFolder,
    string ProjectFolder,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public string? WorkspacePathId { get; init; }
}
