namespace Sunder.Agent.Execution.Common;

public sealed record ExecutableResolution(
    string OriginalFileName,
    string FileName,
    IReadOnlyList<string> EffectivePathEntries,
    IReadOnlyList<string> CheckedLocations,
    bool IsBareExecutableName,
    bool WasResolved);

public static class ExecutableResolver
{
    private static readonly string[] DefaultWindowsPathExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public static ExecutableResolution Resolve(
        string fileName,
        IReadOnlyList<string> effectivePathEntries,
        Func<string, bool> fileExists,
        bool isWindows,
        string? pathExtensions = null)
    {
        if (!IsBareExecutableName(fileName, isWindows))
        {
            return new ExecutableResolution(
                fileName,
                fileName,
                effectivePathEntries,
                [],
                IsBareExecutableName: false,
                WasResolved: fileExists(fileName));
        }

        var checkedLocations = new List<string>();
        foreach (var pathEntry in effectivePathEntries)
        {
            foreach (var executableName in EnumerateExecutableNames(fileName, isWindows, pathExtensions))
            {
                var candidate = CombinePathEntry(pathEntry, executableName, isWindows);
                if (!checkedLocations.Contains(candidate, StringComparer.OrdinalIgnoreCase))
                {
                    checkedLocations.Add(candidate);
                }

                if (fileExists(candidate))
                {
                    return new ExecutableResolution(
                        fileName,
                        candidate,
                        effectivePathEntries,
                        checkedLocations,
                        IsBareExecutableName: true,
                        WasResolved: true);
                }
            }
        }

        return new ExecutableResolution(
            fileName,
            fileName,
            effectivePathEntries,
            checkedLocations,
            IsBareExecutableName: true,
            WasResolved: false);
    }

    private static bool IsBareExecutableName(string fileName, bool isWindows)
        => !string.IsNullOrWhiteSpace(fileName)
           && fileName.IndexOf('/') < 0
           && fileName.IndexOf('\\') < 0
           && (!isWindows || fileName.IndexOf(':') < 0);

    private static IEnumerable<string> EnumerateExecutableNames(string fileName, bool isWindows, string? pathExtensions)
    {
        if (!isWindows || Path.HasExtension(fileName))
        {
            yield return fileName;
            yield break;
        }

        var extensions = string.IsNullOrWhiteSpace(pathExtensions)
            ? DefaultWindowsPathExtensions
            : pathExtensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var extension in extensions)
        {
            yield return fileName + (extension.StartsWith('.') ? extension : "." + extension);
        }
    }

    private static string CombinePathEntry(string pathEntry, string executableName, bool isWindows)
    {
        var separator = isWindows ? '\\' : '/';
        var trimmed = pathEntry.TrimEnd('\\', '/');
        return string.IsNullOrEmpty(trimmed)
            ? separator + executableName
            : trimmed + separator + executableName;
    }
}
