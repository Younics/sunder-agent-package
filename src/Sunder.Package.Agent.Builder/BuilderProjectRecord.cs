namespace Sunder.Package.Agent.Builder;

/// <summary>A local bookmark and last observed source metadata; never an authored project manifest.</summary>
public sealed record BuilderProjectRecord(
    string Id,
    string DisplayName,
    string PackageId,
    string ProjectFolder,
    string? EntryPoint,
    BuilderProjectKind? Kind,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc)
{
    public bool SetupIncomplete { get; init; }
    public bool Watch { get; init; } = true;
    public BuilderToolchainInstallation? Installation { get; init; }
}
