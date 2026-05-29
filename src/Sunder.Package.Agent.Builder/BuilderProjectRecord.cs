namespace Sunder.Package.Agent.Builder;

public sealed record BuilderProjectRecord(
    string Id,
    string DisplayName,
    string PackageId,
    string WorkspaceId,
    string ExecutionProjectFolder,
    string ProjectFolder,
    string DevPackageFolder,
    bool Watch,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public bool AutoLoadOnStartup { get; init; }

    public string? WorkspacePathId { get; init; }

    public string? DevPackageRelativePath { get; init; }
}
