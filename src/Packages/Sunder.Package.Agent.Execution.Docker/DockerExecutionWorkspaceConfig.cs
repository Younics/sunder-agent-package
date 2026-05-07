namespace Sunder.Package.Agent.Execution.Docker;

public sealed record DockerExecutionWorkspaceConfig(
    string? ImageReference,
    IReadOnlyList<string> AllowedRoots,
    string? DefaultWorkingDirectory,
    string? ContainerName,
    string? ShellPath = null);

public sealed record DockerExecutionMount(
    string HostPath,
    string ContainerPath);
