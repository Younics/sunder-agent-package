namespace Sunder.Agent.Execution.Common;

public static class HostPath
{
    private const int MaximumSymbolicLinkDepth = 40;

    public static string ResolvePhysical(string path)
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

    public static bool IsSameOrChild(string candidatePath, string rootPath, bool caseInsensitive)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = caseInsensitive ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison);
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
