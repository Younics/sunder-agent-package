using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerPathResolver
{
    public static string ResolvePath(DockerExecutionRuntimeConfig config, string path, bool allowOutsideConfiguredScope)
    {
        var normalized = ResolveRuntimePath(path, ResolveDefaultBaseDirectory(config));

        if (!allowOutsideConfiguredScope && !IsInsideWorkspacePath(config, normalized))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the configured Docker workspace paths.");
        }

        return normalized;
    }

    public static string ResolveWorkingDirectory(
        DockerExecutionRuntimeConfig config,
        string? requestedWorkingDirectory,
        bool allowOutsideConfiguredScope)
        => string.IsNullOrWhiteSpace(requestedWorkingDirectory)
            ? ResolveDefaultBaseDirectory(config)
            : ResolvePath(config, requestedWorkingDirectory, allowOutsideConfiguredScope);

    public static string ResolveDefaultBaseDirectory(DockerExecutionRuntimeConfig config)
        => string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? config.Mounts.FirstOrDefault()?.ContainerPath
              ?? throw new InvalidOperationException("The Docker execution binding has no workspace paths configured.")
            : config.DefaultWorkingDirectory;

    public static bool IsInsideWorkspacePath(DockerExecutionRuntimeConfig config, string candidate)
        => config.Mounts.Any(mount => DockerExecutionWorkspaceConfigService.IsSameOrChildPath(candidate, mount.ContainerPath));

    public static AgentResolvedResource ResolveFileResource(
        DockerExecutionRuntimeConfig config,
        string path,
        bool allowOutsideConfiguredScope,
        bool exists)
    {
        var resolved = ResolvePath(config, path, allowOutsideConfiguredScope);
        var boundary = IsInsideWorkspacePath(config, resolved)
            ? AgentPermissionBoundaryIds.ConfiguredScope
            : AgentPermissionBoundaryIds.OutsideConfiguredScope;
        return new AgentResolvedResource("file", resolved, resolved, boundary, exists);
    }

    public static AgentExecutionPathMapping MapToHostPath(
        DockerExecutionRuntimeConfig config,
        string executionPath)
    {
        var normalizedPath = ResolvePath(config, executionPath, allowOutsideConfiguredScope: false);
        var mount = config.Mounts
            .Where(mount => DockerExecutionWorkspaceConfigService.IsSameOrChildPath(normalizedPath, mount.ContainerPath))
            .OrderByDescending(mount => mount.ContainerPath.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Execution path is outside the selected workspace paths.");
        var relative = normalizedPath[mount.ContainerPath.Length..].TrimStart('/');
        var hostPath = string.IsNullOrWhiteSpace(relative)
            ? mount.HostPath
            : Path.Combine([mount.HostPath, .. relative.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
        var physicalHostPath = HostPath.ResolvePhysical(hostPath);
        if (!config.Mounts.Any(candidateMount => IsSameOrChildHostPath(
                physicalHostPath,
                HostPath.ResolvePhysical(candidateMount.HostPath))))
        {
            throw new InvalidOperationException($"Execution path '{executionPath}' resolves outside the configured Docker host mount paths.");
        }

        return new AgentExecutionPathMapping(normalizedPath, physicalHostPath, IsInsideAllowedRoot: true);
    }

    private static bool IsSameOrChildHostPath(string candidatePath, string rootPath)
        => HostPath.IsSameOrChild(
            candidatePath,
            rootPath,
            caseInsensitive: OperatingSystem.IsWindows() || OperatingSystem.IsMacOS());

    private static string ResolveRuntimePath(string path, string baseDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(path)
            ? baseDirectory
            : path.Trim().Replace('\\', '/');
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            candidate = DockerExecutionWorkspaceConfigService.NormalizeContainerPath(baseDirectory) + "/" + candidate;
        }

        var segments = new List<string>();
        foreach (var segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case ".." when segments.Count > 0:
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                case "..":
                    throw new InvalidOperationException($"Path '{path}' cannot resolve above the container root.");
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return DockerExecutionWorkspaceConfigService.NormalizeContainerPath("/" + string.Join("/", segments));
    }
}
