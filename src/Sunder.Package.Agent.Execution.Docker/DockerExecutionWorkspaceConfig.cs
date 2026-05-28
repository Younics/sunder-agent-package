namespace Sunder.Package.Agent.Execution.Docker;

public sealed record DockerExecutionWorkspaceConfig(
    string? ImageReference,
    string? ContainerName,
    string? ShellPath = null,
    IReadOnlyList<string>? PathEntries = null);

public sealed record DockerExecutionMount(
    string HostPath,
    string ContainerPath);

internal sealed record DockerExecutionRuntimeConfig(
    string? ImageReference,
    string? ContainerName,
    string? ShellPath,
    IReadOnlyList<string>? PathEntries,
    IReadOnlyList<DockerExecutionMount> Mounts,
    string? DefaultWorkingDirectory);
