namespace Sunder.Package.Agent.Builder;

/// <summary>A source-only inspection. No project evaluation, restore, or user code is run.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderProjectInspection
{
    public required BuilderProjectKind Kind { get; init; }
    public required string ProjectFolder { get; init; }
    public required string EntryPoint { get; init; }
    public required string DisplayName { get; init; }
    public required string PackageId { get; init; }
    public required string SourceRevision { get; init; }
    public string? TargetFramework { get; init; }
    public bool CanEditDisplayName { get; init; }
    public bool CanEditPackageId { get; init; }
    public string? ReadOnlyReason { get; init; }
    public string? PackageIdReadOnlyReason { get; init; }
    public bool RequiresNodeToolingForIdEdit { get; init; }
    public BuilderProjectToolchainAssessment? ToolchainAssessment { get; init; }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record BuilderProjectToolchainAssessment(bool RequiresUpgrade, bool CanUpgrade, string? Message);

public sealed class BuilderProjectConflictException(string message) : IOException(message);
