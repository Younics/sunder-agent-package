using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerPathResolver
{
    public static string ResolvePath(DockerExecutionWorkspaceConfig config, string path, bool allowOutsideConfiguredScope)
    {
        var normalized = ResolveRuntimePath(path, ResolveDefaultBaseDirectory(config));

        if (!allowOutsideConfiguredScope && !IsInsideAllowedRoot(config, normalized))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the Docker workspace allowed roots.");
        }

        return normalized;
    }

    public static string ResolveWorkingDirectory(
        DockerExecutionWorkspaceConfig config,
        string? requestedWorkingDirectory,
        bool allowOutsideConfiguredScope)
        => string.IsNullOrWhiteSpace(requestedWorkingDirectory)
            ? ResolveDefaultBaseDirectory(config)
            : ResolvePath(config, requestedWorkingDirectory, allowOutsideConfiguredScope);

    public static string ResolveDefaultBaseDirectory(DockerExecutionWorkspaceConfig config)
        => string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? config.AllowedRoots.FirstOrDefault() ?? DockerExecutionWorkspaceConfigService.DefaultContainerRoot
            : config.DefaultWorkingDirectory;

    public static bool IsInsideAllowedRoot(DockerExecutionWorkspaceConfig config, string candidate)
        => config.AllowedRoots.Any(root => DockerExecutionWorkspaceConfigService.IsSameOrChildPath(candidate, root));

    public static AgentResolvedResource ResolveFileResource(
        DockerExecutionWorkspaceConfig config,
        string path,
        bool allowOutsideConfiguredScope)
    {
        var resolved = ResolvePath(config, path, allowOutsideConfiguredScope);
        var boundary = IsInsideAllowedRoot(config, resolved)
            ? AgentPermissionBoundaryIds.ConfiguredScope
            : AgentPermissionBoundaryIds.OutsideConfiguredScope;
        return new AgentResolvedResource("file", resolved, resolved, boundary, Exists: true);
    }

    public static AgentExecutionPathMapping MapToHostPath(
        DockerExecutionWorkspaceConfigService configService,
        DockerExecutionWorkspaceConfig config,
        string executionPath)
    {
        var normalizedPath = ResolvePath(config, executionPath, allowOutsideConfiguredScope: false);
        var root = config.AllowedRoots
            .Where(root => DockerExecutionWorkspaceConfigService.IsSameOrChildPath(normalizedPath, root))
            .OrderByDescending(root => root.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("Execution path is outside the selected workspace allowed roots.");
        var hostRoot = configService.ResolveHostPath(config, root);
        var relative = normalizedPath[root.Length..].TrimStart('/');
        var hostPath = string.IsNullOrWhiteSpace(relative)
            ? hostRoot
            : Path.Combine([hostRoot, .. relative.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)]);
        return new AgentExecutionPathMapping(normalizedPath, Path.GetFullPath(hostPath), IsInsideAllowedRoot(config, normalizedPath));
    }

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
