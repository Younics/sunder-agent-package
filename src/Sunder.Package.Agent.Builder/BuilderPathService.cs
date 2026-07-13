using Sunder.Package.Agent.Contracts.Models;

using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

public sealed class BuilderPathService
{
    public const string DefaultDevPackageRelativePath = "/bin/Debug/net10.0/sunder-dev";
    private const int MaximumSymbolicLinkDepth = 40;

    public BuilderProjectRecord NormalizeProject(BuilderProjectRecord project)
    {
        var relativePath = string.IsNullOrWhiteSpace(project.DevPackageRelativePath)
            ? TryResolveRelativeDevPackagePath(project.ProjectFolder, project.DevPackageFolder) ?? DefaultDevPackageRelativePath
            : project.DevPackageRelativePath;
        relativePath = NormalizeDevPackageRelativePath(relativePath);
        var devPackageFolder = string.IsNullOrWhiteSpace(project.ProjectFolder)
            ? project.DevPackageFolder
            : ResolveDevPackageFolder(project.ProjectFolder, relativePath);
        return project with
        {
            DevPackageRelativePath = relativePath,
            DevPackageFolder = devPackageFolder,
        };
    }

    public string NormalizeDevPackageRelativePath(string? relativePath)
    {
        var value = string.IsNullOrWhiteSpace(relativePath)
            ? DefaultDevPackageRelativePath
            : relativePath.Trim().Replace('\\', '/');
        value = "/" + value.TrimStart('/');
        if (value.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Any(part => part == ".."))
        {
            throw new InvalidOperationException("sunder-dev folder must stay inside the package project folder.");
        }

        return value;
    }

    public string ResolveDevPackageFolder(BuilderProjectRecord project)
        => ResolveDevPackageFolder(project.ProjectFolder, project.DevPackageRelativePath ?? DefaultDevPackageRelativePath);

    public string ResolveDevPackageFolder(string projectFolder, string devPackageRelativePath)
    {
        var normalized = NormalizeDevPackageRelativePath(devPackageRelativePath);
        var parts = normalized.TrimStart('/', '\\')
            .Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return Path.GetFullPath(Path.Combine([Path.GetFullPath(projectFolder), .. parts]));
    }

    public string ResolveContainedHostPath(string hostPath, string allowedRoot)
    {
        var physicalPath = ResolvePhysicalPath(hostPath);
        var physicalRoot = ResolvePhysicalPath(allowedRoot);
        if (!IsSameOrChildPath(physicalPath, physicalRoot))
        {
            throw new InvalidOperationException($"Host path '{hostPath}' resolves outside the selected workspace path.");
        }

        return physicalPath;
    }

    public string? TryResolveRelativeDevPackagePath(string projectFolder, string devPackageFolder)
    {
        if (string.IsNullOrWhiteSpace(projectFolder) || string.IsNullOrWhiteSpace(devPackageFolder))
        {
            return null;
        }

        var projectRoot = Path.GetFullPath(projectFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var devFolder = Path.GetFullPath(devPackageFolder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = PathComparison;
        if (!string.Equals(projectRoot, devFolder, comparison)
            && !devFolder.StartsWith(projectRoot + Path.DirectorySeparatorChar, comparison)
            && !devFolder.StartsWith(projectRoot + Path.AltDirectorySeparatorChar, comparison))
        {
            return null;
        }

        var relative = Path.GetRelativePath(projectRoot, devFolder).Replace(Path.DirectorySeparatorChar, '/');
        return string.IsNullOrWhiteSpace(relative) || relative == "."
            ? "/"
            : NormalizeDevPackageRelativePath(relative);
    }

    public string ResolveExecutionProjectFolder(BuilderProjectRecord project)
        => string.IsNullOrWhiteSpace(project.ExecutionProjectFolder)
            ? project.ProjectFolder
            : project.ExecutionProjectFolder;

    public string ResolveExecutionProjectFolder(
        BuilderWorkspaceExecution execution,
        AgentWorkspacePathRecord workspacePath,
        string displayName)
    {
        var root = execution.ResolveExecutionWorkspacePath(workspacePath);
        return execution.CombinePath(root, ToProjectName(displayName));
    }

    public bool IsProjectInitialized(BuilderProjectRecord? project)
    {
        if (project is null || string.IsNullOrWhiteSpace(project.ProjectFolder) || !Directory.Exists(project.ProjectFolder))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(project.ProjectFolder, "*.csproj", SearchOption.TopDirectoryOnly)
                .Any(path => !IsContractsProjectFile(path));
        }
        catch
        {
            return false;
        }
    }

    public string FormatWorkspacePath(string hostPath)
    {
        if (string.IsNullOrWhiteSpace(hostPath))
        {
            return string.Empty;
        }

        var fullPath = Path.GetFullPath(hostPath.Trim());
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            var normalizedHome = Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), normalizedHome, PathComparison)
                || fullPath.StartsWith(normalizedHome + Path.DirectorySeparatorChar, PathComparison)
                || fullPath.StartsWith(normalizedHome + Path.AltDirectorySeparatorChar, PathComparison))
            {
                var relative = fullPath[normalizedHome.Length..]
                    .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    .Replace(Path.DirectorySeparatorChar, '/');
                return string.IsNullOrWhiteSpace(relative) ? "~" : $"~/{relative}";
            }
        }

        return fullPath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public string? GetExecutionParentFolder(string path)
    {
        var trimmed = path.TrimEnd('/', '\\');
        var separatorIndex = Math.Max(trimmed.LastIndexOf('/'), trimmed.LastIndexOf('\\'));
        if (separatorIndex <= 0)
        {
            return null;
        }

        if (trimmed.Length > 2 && trimmed[1] == ':' && separatorIndex == 2)
        {
            return trimmed[..3];
        }

        return trimmed[..separatorIndex];
    }

    public string ToProjectName(string displayName)
    {
        var characters = displayName.Where(char.IsLetterOrDigit).ToArray();
        return characters.Length == 0 ? "SunderPackage" : new string(characters);
    }

    public string ToPackageId(string displayName)
    {
        var parts = displayName
            .ToLowerInvariant()
            .Split([' ', '-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => new string(part.Where(static character => character is >= 'a' and <= 'z' or >= '0' and <= '9').ToArray()))
            .Where(part => part.Length > 0)
            .ToArray();
        if (parts.Length == 0)
        {
            return "local.sunder.package";
        }

        var packageId = "local." + string.Join('.', parts);
        if (packageId.Length > PackageId.MaximumLength)
        {
            packageId = packageId[..PackageId.MaximumLength].TrimEnd('.');
        }

        return PackageId.TryParse(packageId, out _) ? packageId : "local.sunder.package";
    }

    public static bool IsContractsProjectFile(string path)
        => Path.GetFileNameWithoutExtension(path).EndsWith(".Contracts", StringComparison.OrdinalIgnoreCase);

    private static string ResolvePhysicalPath(string path)
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

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, root, PathComparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, PathComparison);
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}
