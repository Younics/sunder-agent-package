using System.Diagnostics;
using System.Text;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

internal static class DockerCli
{
    internal const string ExecutablePathConfigurationKey = "docker.executablePath";

    private const string ExecutablePathEnvironmentVariable = "SUNDER_DOCKER_CLI_PATH";
    private const string PathEnvironmentVariable = "PATH";

    private static readonly string[] UnixFallbackPaths =
    [
        "/usr/local/bin/docker",
        "/opt/homebrew/bin/docker",
        "/Applications/Docker.app/Contents/Resources/bin/docker",
        "/usr/bin/docker",
        "/snap/bin/docker",
    ];

    private static readonly string[] WindowsFallbackPaths =
    [
        @"C:\Program Files\Docker\Docker\resources\bin\docker.exe",
    ];

    internal static ProcessStartInfo CreateStartInfo(
        IPackageContext packageContext,
        IReadOnlyList<string> args,
        bool redirectStandardInput)
    {
        var resolution = ResolveExecutable(packageContext);
        if (resolution.ExecutablePath is null)
        {
            throw new InvalidOperationException(resolution.FailureMessage);
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = resolution.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = redirectStandardInput,
            CreateNoWindow = true,
        };
        if (redirectStandardInput)
        {
            startInfo.StandardInputEncoding = Encoding.UTF8;
        }

        foreach (var arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        AugmentPath(startInfo, resolution.ExecutablePath);
        return startInfo;
    }

    internal static DockerCliResolution ResolveExecutable(IPackageContext packageContext)
        => ResolveExecutable(
            packageContext.Storage.State.GetValue(ExecutablePathConfigurationKey)
            ?? packageContext.Configuration.GetValue(ExecutablePathConfigurationKey),
            Environment.GetEnvironmentVariable(ExecutablePathEnvironmentVariable),
            Environment.GetEnvironmentVariable(PathEnvironmentVariable),
            OperatingSystem.IsWindows() ? WindowsFallbackPaths : UnixFallbackPaths,
            File.Exists,
            OperatingSystem.IsWindows());

    internal static DockerCliResolution ResolveExecutable(
        string? configuredPath,
        string? environmentPath,
        string? pathValue,
        IReadOnlyList<string> fallbackPaths,
        Func<string, bool> fileExists,
        bool isWindows)
    {
        var checkedLocations = new List<string>();
        foreach (var candidate in EnumerateCandidates(configuredPath, environmentPath, pathValue, fallbackPaths, isWindows))
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var normalized = candidate.Trim().Trim('"');
            if (checkedLocations.Contains(normalized, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            checkedLocations.Add(normalized);
            if (fileExists(normalized))
            {
                return new DockerCliResolution(normalized, checkedLocations, null);
            }
        }

        return new DockerCliResolution(null, checkedLocations, BuildNotFoundMessage(checkedLocations));
    }

    private static IEnumerable<string> EnumerateCandidates(
        string? configuredPath,
        string? environmentPath,
        string? pathValue,
        IReadOnlyList<string> fallbackPaths,
        bool isWindows)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            yield return configuredPath;
        }

        if (!string.IsNullOrWhiteSpace(environmentPath))
        {
            yield return environmentPath;
        }

        foreach (var path in EnumeratePathCandidates(pathValue, isWindows))
        {
            yield return path;
        }

        foreach (var fallbackPath in fallbackPaths)
        {
            yield return fallbackPath;
        }
    }

    private static IEnumerable<string> EnumeratePathCandidates(string? pathValue, bool isWindows)
    {
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            yield break;
        }

        foreach (var path in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            yield return Path.Combine(path, isWindows ? "docker.exe" : "docker");
        }
    }

    private static void AugmentPath(ProcessStartInfo startInfo, string dockerPath)
    {
        var pathParts = new List<string>();
        AddPathPart(pathParts, Path.GetDirectoryName(dockerPath));
        if (!OperatingSystem.IsWindows())
        {
            AddPathPart(pathParts, "/usr/local/bin");
            AddPathPart(pathParts, "/opt/homebrew/bin");
            AddPathPart(pathParts, "/Applications/Docker.app/Contents/Resources/bin");
        }

        var existingPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        foreach (var path in (existingPath ?? string.Empty).Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            AddPathPart(pathParts, path);
        }

        startInfo.Environment[PathEnvironmentVariable] = string.Join(Path.PathSeparator, pathParts);
    }

    private static void AddPathPart(List<string> pathParts, string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        if (!pathParts.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            pathParts.Add(path);
        }
    }

    private static string BuildNotFoundMessage(IReadOnlyList<string> checkedLocations)
    {
        var builder = new StringBuilder("Docker CLI was not found. Install Docker Desktop or set Docker CLI path in Docker Execution settings.");
        if (checkedLocations.Count > 0)
        {
            builder.Append(" Checked: ").Append(string.Join(", ", checkedLocations)).Append('.');
        }

        return builder.ToString();
    }
}

internal sealed record DockerCliResolution(
    string? ExecutablePath,
    IReadOnlyList<string> CheckedLocations,
    string? FailureMessage);
