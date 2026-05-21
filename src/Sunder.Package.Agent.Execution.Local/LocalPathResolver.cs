namespace Sunder.Package.Agent.Execution.Local;

internal static class LocalPathResolver
{
    public static string ResolvePath(LocalExecutionWorkspaceConfig config, string path, bool allowOutsideConfiguredScope)
        => ResolvePathFromBase(config, path, ResolveDefaultBaseDirectory(config), allowOutsideConfiguredScope);

    public static string ResolveWorkingDirectory(LocalExecutionWorkspaceConfig config, string? requestedWorkingDirectory, bool allowOutsideConfiguredScope)
        => string.IsNullOrWhiteSpace(requestedWorkingDirectory)
            ? ResolveDefaultBaseDirectory(config)
            : ResolvePathFromBase(config, requestedWorkingDirectory, ResolveDefaultBaseDirectory(config), allowOutsideConfiguredScope);

    public static string ResolveDefaultBaseDirectory(LocalExecutionWorkspaceConfig config)
        => string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? ResolveRoot(config)
            : config.DefaultWorkingDirectory;

    public static string ResolveRoot(LocalExecutionWorkspaceConfig config)
        => config.AllowedRoots.Count == 0
            ? throw new InvalidOperationException("The local execution binding has no allowed roots configured.")
            : config.AllowedRoots[0];

    public static bool IsInsideAllowedRoot(LocalExecutionWorkspaceConfig config, string candidate)
        => config.AllowedRoots.Any(root => LocalExecutionWorkspaceConfigService.IsSameOrChildPath(candidate, root));

    private static string ResolvePathFromBase(LocalExecutionWorkspaceConfig config, string path, string baseDirectory, bool allowOutsideConfiguredScope)
    {
        var expandedPath = LocalExecutionWorkspaceConfigService.ExpandPath(path);
        var candidate = Path.IsPathRooted(expandedPath)
            ? Path.GetFullPath(expandedPath)
            : Path.GetFullPath(Path.Combine(baseDirectory, expandedPath));

        if (!allowOutsideConfiguredScope && !IsInsideAllowedRoot(config, candidate))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the workspace allowed roots.");
        }

        return candidate;
    }
}
