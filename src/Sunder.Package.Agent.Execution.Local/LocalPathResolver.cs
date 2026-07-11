namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalPathResolver
{
    private const int MaximumSymbolicLinkDepth = 40;

    public static string ResolvePath(LocalExecutionRuntimeConfig config, string path, bool allowOutsideConfiguredScope)
        => ResolvePathFromBase(config, path, ResolveDefaultBaseDirectory(config), allowOutsideConfiguredScope);

    public static string ResolveFileSystemPath(LocalExecutionRuntimeConfig config, string path, bool allowOutsideConfiguredScope)
    {
        var candidate = ResolvePath(config, path, allowOutsideConfiguredScope);
        var physicalCandidate = ResolvePhysicalPath(candidate);

        if (!allowOutsideConfiguredScope && !IsInsideResolvedPhysicalWorkspacePath(config, physicalCandidate))
        {
            throw new InvalidOperationException($"Path '{path}' resolves outside the configured workspace paths.");
        }

        return physicalCandidate;
    }

    public static string ResolveWorkingDirectory(LocalExecutionRuntimeConfig config, string? requestedWorkingDirectory, bool allowOutsideConfiguredScope)
    {
        var requested = string.IsNullOrWhiteSpace(requestedWorkingDirectory) ? "." : requestedWorkingDirectory;
        var candidate = ResolvePathFromBase(
            config,
            requested,
            ResolveDefaultBaseDirectory(config),
            allowOutsideConfiguredScope);
        var physicalCandidate = ResolvePhysicalPath(candidate);
        if (!allowOutsideConfiguredScope && !IsInsideResolvedPhysicalWorkspacePath(config, physicalCandidate))
        {
            throw new InvalidOperationException($"Working directory '{requestedWorkingDirectory ?? candidate}' resolves outside the configured workspace paths.");
        }

        return physicalCandidate;
    }

    public static string ResolveDefaultBaseDirectory(LocalExecutionRuntimeConfig config)
        => string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? ResolveRoot(config)
            : config.DefaultWorkingDirectory;

    public static string ResolveRoot(LocalExecutionRuntimeConfig config)
        => config.WorkspacePaths.Count == 0
            ? throw new InvalidOperationException("The local execution binding has no workspace paths configured.")
            : config.WorkspacePaths[0];

    public static bool IsInsideWorkspacePath(LocalExecutionRuntimeConfig config, string candidate)
        => config.WorkspacePaths.Any(root => LocalExecutionWorkspaceConfigService.IsSameOrChildPath(candidate, root));

    public static bool IsInsidePhysicalWorkspacePath(LocalExecutionRuntimeConfig config, string candidate)
        => IsInsideResolvedPhysicalWorkspacePath(config, ResolvePhysicalPath(candidate));

    public static string ResolvePhysicalPath(string path)
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

    private static bool IsInsideResolvedPhysicalWorkspacePath(
        LocalExecutionRuntimeConfig config,
        string physicalCandidate)
    {
        return config.WorkspacePaths.Any(root =>
            LocalExecutionWorkspaceConfigService.IsSameOrChildPath(physicalCandidate, ResolvePhysicalPath(root)));
    }

    private static string ResolvePathFromBase(LocalExecutionRuntimeConfig config, string path, string baseDirectory, bool allowOutsideConfiguredScope)
    {
        var expandedPath = LocalExecutionWorkspaceConfigService.ExpandPath(path);
        var candidate = Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath)
            : Path.GetFullPath(Path.Combine(baseDirectory, expandedPath));

        if (!allowOutsideConfiguredScope && !IsInsideWorkspacePath(config, candidate))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the configured workspace paths.");
        }

        return candidate;
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
}
