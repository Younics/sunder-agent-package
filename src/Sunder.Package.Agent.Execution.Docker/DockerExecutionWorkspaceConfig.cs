namespace Sunder.Package.Agent.Execution.Docker;

public sealed record DockerExecutionWorkspaceConfig(
    string? ImageReference,
    string? ContainerName,
    string? ShellPath = null,
    IReadOnlyList<string>? PathEntries = null,
    int SchemaVersion = DockerImageCatalogService.CurrentSchemaVersion,
    bool ImageReferenceNeedsAttention = false);

public sealed record DockerExecutionMount(
    string HostPath,
    string ContainerPath);

internal sealed record DockerExecutionRuntimeConfig(
    string? ImageReference,
    string? ContainerName,
    string? ShellPath,
    IReadOnlyList<string>? PathEntries,
    IReadOnlyList<DockerExecutionMount> Mounts,
    string? DefaultWorkingDirectory,
    bool ImageReferenceNeedsAttention = false)
{
    public string? DockerCliPath { get; init; }

    public int? DefaultTimeoutSeconds { get; init; }

    public string? ImageIdentity { get; init; }
}

internal sealed record DockerExecutionConfigurationSnapshot(
    DockerExecutionWorkspaceConfig WorkspaceConfig,
    int DefaultTimeoutSeconds)
{
    public string? DockerCliPath { get; init; }

    public string DockerCliResolution { get; init; } = string.Empty;

    public string? ImageIdentity { get; init; }
}
