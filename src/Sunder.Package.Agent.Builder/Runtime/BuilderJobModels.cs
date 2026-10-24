using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
public sealed record BuilderJobRequest
{
    /// <summary>Client-generated retry identity. Reuse this ID only with the same request.</summary>
    public string? Id { get; init; }
    /// <summary>The exact backend incarnation selected before job acceptance.</summary>
    public string? BackendId { get; init; }
    public required string Operation { get; init; }
    public required BuilderProjectKind Kind { get; init; }
    public string? ProjectFolder { get; init; }
    public string? EntryPoint { get; init; }
    public string? DisplayName { get; init; }
    public string? PackageId { get; init; }
    public BuilderProjectInspection? Expected { get; init; }
    public BuilderToolchainInstallation? Installation { get; init; }
}

[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
public sealed record BuilderJobSnapshot
{
    public required string Id { get; init; }
    public string? BackendId { get; init; }
    public required string Operation { get; init; }
    public required string State { get; init; }
    public required string Message { get; init; }
    public required string Output { get; init; }
    public string? Error { get; init; }
    public BuilderProjectInspection? Project { get; init; }
    public string? DevelopmentFolder { get; init; }
    public string? DevelopmentContentIdentity { get; init; }
    public BuilderToolchainInstallation? Installation { get; init; }
    public string? ArchivePath { get; init; }
    public BuilderJobPhase Phase { get; init; }
    public DateTimeOffset? LastSourceChangeAtUtc { get; init; }
    public DateTimeOffset? LastSuccessfulBuildAtUtc { get; init; }
    /// <summary>App-side observation state, never an invented backend job outcome.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public BuilderJobConnectionState ConnectionState { get; init; }
}

[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
public enum BuilderJobConnectionState { Connected, Reconnecting, StopUnconfirmed }

[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
public enum BuilderJobPhase { Queued, CheckingSdk, Restoring, Building, Validating, BuildReady, Packaging, EditingMetadata, BuildFailed }

public sealed record BuilderJobUpdate
{
    public BuilderJobPhase? Phase { get; init; }
    public DateTimeOffset? LastSourceChangeAtUtc { get; init; }
    public DateTimeOffset? LastSuccessfulBuildAtUtc { get; init; }
    public string? Message { get; init; }
    public BuilderProjectInspection? Project { get; init; }
    public string? DevelopmentFolder { get; init; }
    public string? DevelopmentContentIdentity { get; init; }
    public BuilderToolchainInstallation? Installation { get; init; }
    public string? ArchivePath { get; init; }
}

public interface IBuilderJobReporter : IProgress<string>
{
    void Update(BuilderJobUpdate update);
}

public interface IBuilderJobDriver
{
    Task ExecuteAsync(BuilderJobRequest request, IBuilderJobReporter reporter, CancellationToken cancellationToken);
}

public sealed record BuilderJobOptions
{
    public int MaximumConcurrentJobs { get; init; } = 4;
    public int CompletedJobLimit { get; init; } = 64;
    public int MaximumOutputCharacters { get; init; } = 128 * 1024;
    public TimeSpan OrphanTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public TimeSpan HeartbeatInterval { get; init; } = TimeSpan.FromSeconds(5);
    public TimeSpan FiniteOperationTimeout { get; init; } = TimeSpan.FromMinutes(30);
}

public sealed class BuilderJobBusyException(string message) : InvalidOperationException(message);
public sealed class BuilderJobNotFoundException(string message) : InvalidOperationException(message);
