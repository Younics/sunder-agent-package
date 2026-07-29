using Sunder.Agent.Execution.Common;

namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalPathResolver
{
    public static string ResolvePath(LocalExecutionRuntimeConfig config, string path, bool allowOutsideConfiguredScope)
        => ResolvePathFromBase(config, path, ResolveDefaultBaseDirectory(config), allowOutsideConfiguredScope);

    // Working-directory resolution belongs to unrestricted shell/process execution, not to the
    // structured no-follow filesystem guarantee implemented by LocalSecurePathEngine.
    public static string ResolveWorkingDirectory(LocalExecutionRuntimeConfig config, string? requestedWorkingDirectory, bool allowOutsideConfiguredScope)
    {
        var requested = string.IsNullOrWhiteSpace(requestedWorkingDirectory) ? "." : requestedWorkingDirectory;
        var candidate = ResolvePathFromBase(
            config,
            requested,
            ResolveDefaultBaseDirectory(config),
            allowOutsideConfiguredScope);
        var physicalCandidate = HostPath.ResolvePhysical(candidate);
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

    public static string ResolvePhysicalPath(string path)
        => HostPath.ResolvePhysical(path);

    private static bool IsInsideResolvedPhysicalWorkspacePath(
        LocalExecutionRuntimeConfig config,
        string physicalCandidate)
    {
        return config.WorkspacePaths.Any(root =>
            HostPath.IsSameOrChild(
                physicalCandidate,
                HostPath.ResolvePhysical(root),
                caseInsensitive: OperatingSystem.IsWindows()));
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

}
