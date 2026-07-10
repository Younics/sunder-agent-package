using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpCommandResolver
{
    private static readonly TimeSpan ShellProbeTimeout = TimeSpan.FromSeconds(2);

    public static async Task<McpCommandResolution> ResolveAsync(
        string command,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var effectiveEnvironment = new Dictionary<string, string>(environmentVariables, StringComparer.OrdinalIgnoreCase);
        var effectivePath = await ResolveEffectivePathAsync(effectiveEnvironment, logger, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(effectivePath))
        {
            effectiveEnvironment["PATH"] = effectivePath;
        }

        var resolvedCommand = HasPathSeparator(command)
            ? command
            : ResolveBareCommand(command, effectivePath) ?? command;

        return new McpCommandResolution(
            resolvedCommand,
            ResolveWorkingDirectory(workingDirectory),
            effectiveEnvironment.Count == 0 ? null : effectiveEnvironment);
    }

    internal static string? ResolveBareCommand(string command, string? path)
    {
        if (string.IsNullOrWhiteSpace(command) || HasPathSeparator(command))
        {
            return null;
        }

        foreach (var directory in EnumeratePathDirectories(path))
        {
            var candidate = Path.Combine(directory, command);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            if (OperatingSystem.IsWindows())
            {
                foreach (var extension in EnumerateWindowsPathExtensions())
                {
                    var extendedCandidate = candidate + extension;
                    if (File.Exists(extendedCandidate))
                    {
                        return extendedCandidate;
                    }
                }
            }
        }

        return null;
    }

    internal static string? ResolveWorkingDirectory(string? configuredWorkingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(configuredWorkingDirectory))
        {
            return configuredWorkingDirectory.Trim();
        }

        var currentDirectory = Environment.CurrentDirectory;
        if (!string.IsNullOrWhiteSpace(currentDirectory)
            && Directory.Exists(currentDirectory)
            && !string.Equals(Path.GetFullPath(currentDirectory), Path.GetPathRoot(currentDirectory), StringComparison.Ordinal))
        {
            return null;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrWhiteSpace(home) || !Directory.Exists(home) ? null : home;
    }

    private static async Task<string?> ResolveEffectivePathAsync(
        IReadOnlyDictionary<string, string> environmentVariables,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        environmentVariables.TryGetValue("PATH", out var configuredPath);
        var processPath = Environment.GetEnvironmentVariable("PATH");
        var shellPath = await TryReadLoginShellPathAsync(logger, cancellationToken).ConfigureAwait(false);
        return MergePaths(configuredPath, processPath, shellPath, BuildCommonToolPaths());
    }

    private static async Task<string?> TryReadLoginShellPathAsync(ILogger logger, CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            return null;
        }

        var shell = Environment.GetEnvironmentVariable("SHELL");
        if (string.IsNullOrWhiteSpace(shell) || !File.Exists(shell))
        {
            shell = File.Exists("/bin/zsh") ? "/bin/zsh" : File.Exists("/bin/bash") ? "/bin/bash" : null;
        }

        if (string.IsNullOrWhiteSpace(shell))
        {
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ShellProbeTimeout);
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo(shell)
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                },
            };
            process.StartInfo.ArgumentList.Add("-lc");
            process.StartInfo.ArgumentList.Add("printf '%s' \"$PATH\"");

            if (!process.Start())
            {
                return null;
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? (await outputTask.ConfigureAwait(false)).Trim() : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogDebug("Timed out while probing login-shell PATH for MCP command resolution.");
            return null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Failed to probe login-shell PATH for MCP command resolution.");
            return null;
        }
    }

    private static string MergePaths(params object?[] values)
    {
        var paths = new List<string>();
        foreach (var value in values)
        {
            switch (value)
            {
                case string text:
                    paths.AddRange(EnumeratePathDirectories(text));
                    break;
                case IEnumerable<string> directories:
                    paths.AddRange(directories.Where(directory => !string.IsNullOrWhiteSpace(directory)));
                    break;
            }
        }

        return string.Join(Path.PathSeparator, paths.Distinct(StringComparer.Ordinal).Where(Directory.Exists));
    }

    private static IEnumerable<string> EnumeratePathDirectories(string? path)
        => string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    private static IEnumerable<string> BuildCommonToolPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(home))
        {
            yield return Path.Combine(home, ".local", "bin");
            yield return Path.Combine(home, ".npm-global", "bin");
            yield return Path.Combine(home, ".bun", "bin");
            yield return Path.Combine(home, ".deno", "bin");
            yield return Path.Combine(home, ".cargo", "bin");
        }

        yield return "/opt/homebrew/bin";
        yield return "/usr/local/bin";
        yield return "/usr/bin";
        yield return "/bin";
        yield return "/usr/sbin";
        yield return "/sbin";
    }

    private static IEnumerable<string> EnumerateWindowsPathExtensions()
    {
        var extensions = Environment.GetEnvironmentVariable("PATHEXT");
        return string.IsNullOrWhiteSpace(extensions)
            ? [".exe", ".cmd", ".bat"]
            : extensions.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static bool HasPathSeparator(string command)
        => command.Contains(Path.DirectorySeparatorChar)
           || command.Contains(Path.AltDirectorySeparatorChar)
           || Path.IsPathRooted(command);
}

internal sealed record McpCommandResolution(
    string Command,
    string? WorkingDirectory,
    IReadOnlyDictionary<string, string>? EnvironmentVariables);
