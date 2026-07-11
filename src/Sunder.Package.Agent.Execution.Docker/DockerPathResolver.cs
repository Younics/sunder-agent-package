using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerPathResolver
{
    private const int MaximumSymbolicLinkDepth = 40;

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
        var physicalHostPath = ResolvePhysicalHostPath(hostPath);
        if (!config.Mounts.Any(candidateMount => IsSameOrChildHostPath(
                physicalHostPath,
                ResolvePhysicalHostPath(candidateMount.HostPath))))
        {
            throw new InvalidOperationException($"Execution path '{executionPath}' resolves outside the configured Docker host mount paths.");
        }

        return new AgentExecutionPathMapping(normalizedPath, physicalHostPath, IsInsideAllowedRoot: true);
    }

    private static string ResolvePhysicalHostPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var linksFollowed = 0;

        while (true)
        {
            var root = Path.GetPathRoot(fullPath)
                ?? throw new InvalidOperationException($"Path '{path}' has no filesystem root.");
            var pendingSegments = new Queue<string>(fullPath[root.Length..]
                .Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries));
            var current = root;
            var redirected = false;

            while (pendingSegments.TryDequeue(out var segment))
            {
                current = Path.Combine(current, segment);
                if (TryResolveSymbolicLink(current, out var linkTarget))
                {
                    if (++linksFollowed > MaximumSymbolicLinkDepth)
                    {
                        throw new IOException($"Too many symbolic links were encountered while resolving '{path}'.");
                    }

                    fullPath = pendingSegments.Count == 0
                        ? linkTarget
                        : Path.Combine([linkTarget, .. pendingSegments]);
                    fullPath = Path.GetFullPath(fullPath);
                    redirected = true;
                    break;
                }

                if (!Directory.Exists(current) && !File.Exists(current))
                {
                    return pendingSegments.Count == 0
                        ? current
                        : Path.GetFullPath(Path.Combine([current, .. pendingSegments]));
                }
            }

            if (!redirected)
            {
                return Path.GetFullPath(current);
            }
        }
    }

    private static bool TryResolveSymbolicLink(string path, out string targetPath)
    {
        FileSystemInfo[] candidates = Directory.Exists(path)
            ? [new DirectoryInfo(path)]
            : File.Exists(path)
                ? [new FileInfo(path)]
                : [new FileInfo(path), new DirectoryInfo(path)];

        foreach (var candidate in candidates)
        {
            string? reportedTarget;
            try
            {
                reportedTarget = candidate.LinkTarget;
            }
            catch (IOException)
            {
                continue;
            }

            if (reportedTarget is null)
            {
                continue;
            }

            targetPath = Path.GetFullPath(
                Path.IsPathRooted(reportedTarget)
                    ? reportedTarget
                    : Path.Combine(Path.GetDirectoryName(path)!, reportedTarget));
            return true;
        }

        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new IOException($"Reparse point '{path}' could not be resolved.");
            }
        }
        catch (FileNotFoundException)
        {
        }
        catch (DirectoryNotFoundException)
        {
        }

        targetPath = string.Empty;
        return false;
    }

    private static bool IsSameOrChildHostPath(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
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
