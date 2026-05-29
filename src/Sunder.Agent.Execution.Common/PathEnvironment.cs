namespace Sunder.Agent.Execution.Common;

public static class PathEnvironment
{
    public static IReadOnlyList<string> BuildPathEntries(
        IReadOnlyList<string>? configuredPathEntries,
        string? inheritedPath,
        IReadOnlyList<string> fallbackPathEntries,
        string? homeDirectory,
        bool isWindows)
    {
        var entries = new List<string>();
        AddPathEntries(entries, configuredPathEntries ?? [], homeDirectory, isWindows);
        AddPathEntries(entries, SplitPath(inheritedPath, isWindows), homeDirectory, isWindows);
        AddPathEntries(entries, fallbackPathEntries, homeDirectory, isWindows);
        return entries;
    }

    private static IEnumerable<string> SplitPath(string? pathValue, bool isWindows)
        => string.IsNullOrWhiteSpace(pathValue)
            ? []
            : pathValue.Split(isWindows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static void AddPathEntries(List<string> entries, IEnumerable<string> candidates, string? homeDirectory, bool isWindows)
    {
        foreach (var candidate in candidates)
        {
            var normalized = NormalizePathEntry(candidate, homeDirectory, isWindows);
            if (!string.IsNullOrWhiteSpace(normalized)
                && !entries.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                entries.Add(normalized);
            }
        }
    }

    private static string NormalizePathEntry(string path, string? homeDirectory, bool isWindows)
    {
        var trimmed = path.Trim().Trim('"');
        if (string.IsNullOrWhiteSpace(homeDirectory))
        {
            return trimmed;
        }

        if (trimmed == "~")
        {
            return homeDirectory;
        }

        if (!trimmed.StartsWith("~/", StringComparison.Ordinal) && !trimmed.StartsWith(@"~\", StringComparison.Ordinal))
        {
            return trimmed;
        }

        var separator = isWindows ? '\\' : '/';
        var relative = isWindows
            ? trimmed[2..].Replace('/', '\\')
            : trimmed[2..].Replace('\\', '/');
        return homeDirectory.TrimEnd('\\', '/') + separator + relative;
    }
}
