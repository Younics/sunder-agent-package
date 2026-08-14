using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Sunder.Package.Agent.Mcp.Services;

internal static class McpCommandResolver
{
    private static readonly TimeSpan ShellProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ShellProbeDrainTimeout = TimeSpan.FromSeconds(2);
    private static readonly string[] DefaultWindowsEnvironmentVariables =
    [
        "APPDATA", "HOMEDRIVE", "HOMEPATH", "LOCALAPPDATA", "PATH", "PATHEXT",
        "PROCESSOR_ARCHITECTURE", "PROGRAMFILES", "SYSTEMDRIVE", "SYSTEMROOT",
        "TEMP", "USERNAME", "USERPROFILE",
    ];
    private static readonly string[] DefaultUnixEnvironmentVariables =
    [
        "HOME", "LOGNAME", "PATH", "SHELL", "TERM", "USER",
    ];

    public static async Task<McpCommandResolution> ResolveAsync(
        string command,
        string? workingDirectory,
        IReadOnlyDictionary<string, string> environmentVariables,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var baseEnvironment = GetDefaultEnvironmentVariables();
        var effectiveEnvironment = baseEnvironment
            .Where(static item => !string.IsNullOrWhiteSpace(item.Key) && item.Value is not null)
            .ToDictionary(static item => item.Key, static item => item.Value!, StringComparer.OrdinalIgnoreCase);
        foreach (var item in environmentVariables)
        {
            effectiveEnvironment[item.Key] = item.Value;
        }

        var effectivePath = await ResolveEffectivePathAsync(
            effectiveEnvironment,
            baseEnvironment,
            logger,
            cancellationToken).ConfigureAwait(false);
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
            BuildSanitizedTransportEnvironment(effectiveEnvironment));
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
        IReadOnlyDictionary<string, string?> baseEnvironment,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        environmentVariables.TryGetValue("PATH", out var configuredPath);
        var processPath = Environment.GetEnvironmentVariable("PATH");
        var shellPath = await TryReadLoginShellPathAsync(baseEnvironment, logger, cancellationToken).ConfigureAwait(false);
        return MergePaths(configuredPath, processPath, shellPath, BuildCommonToolPaths());
    }

    private static Dictionary<string, string?> GetDefaultEnvironmentVariables()
    {
        var names = OperatingSystem.IsWindows()
            ? DefaultWindowsEnvironmentVariables
            : DefaultUnixEnvironmentVariables;
        var result = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var name in names)
        {
            var value = Environment.GetEnvironmentVariable(name);
            if (value is not null && !value.StartsWith("()", StringComparison.Ordinal))
            {
                result[name] = value;
            }
        }

        return result;
    }

    private static IReadOnlyDictionary<string, string?> BuildSanitizedTransportEnvironment(
        IReadOnlyDictionary<string, string> effectiveEnvironment)
    {
        var result = new Dictionary<string, string?>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var key in Environment.GetEnvironmentVariables().Keys)
        {
            if (key is string name)
            {
                result[name] = null;
            }
        }
        foreach (var item in effectiveEnvironment)
        {
            result[item.Key] = item.Value;
        }

        return result;
    }

    private static async Task<string?> TryReadLoginShellPathAsync(
        IReadOnlyDictionary<string, string?> baseEnvironment,
        ILogger logger,
        CancellationToken cancellationToken)
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

        return await TryReadLoginShellPathAsync(
            shell,
            baseEnvironment,
            logger,
            ShellProbeTimeout,
            cancellationToken).ConfigureAwait(false);
    }

    internal static async Task<string?> TryReadLoginShellPathAsync(
        string shell,
        IReadOnlyDictionary<string, string?> baseEnvironment,
        ILogger logger,
        TimeSpan probeTimeout,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(probeTimeout);
        Process? process = null;
        Task<string>? outputTask = null;
        Task<string>? errorTask = null;
        try
        {
            process = new Process
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
            process.StartInfo.Environment.Clear();
            foreach (var item in baseEnvironment)
            {
                if (item.Value is not null)
                {
                    process.StartInfo.Environment[item.Key] = item.Value;
                }
            }

            if (!process.Start())
            {
                return null;
            }

            outputTask = process.StandardOutput.ReadToEndAsync();
            errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(outputTask, errorTask).WaitAsync(timeout.Token).ConfigureAwait(false);
            return process.ExitCode == 0 ? (await outputTask.ConfigureAwait(false)).Trim() : null;
        }
        catch (OperationCanceledException)
        {
            await TerminateAndDrainAsync(process, outputTask, errorTask, logger).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            logger.LogDebug("Timed out while probing login-shell PATH for MCP command resolution.");
            return null;
        }
        catch (Exception ex)
        {
            await TerminateAndDrainAsync(process, outputTask, errorTask, logger).ConfigureAwait(false);
            logger.LogDebug(ex, "Failed to probe login-shell PATH for MCP command resolution.");
            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static async Task TerminateAndDrainAsync(
        Process? process,
        Task<string>? outputTask,
        Task<string>? errorTask,
        ILogger logger)
    {
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to terminate the login-shell PATH probe process tree.");
        }

        using (var drain = new CancellationTokenSource(ShellProbeDrainTimeout))
        {
            try
            {
                await process.WaitForExitAsync(drain.Token).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                logger.LogDebug(exception, "Failed to drain the login-shell PATH probe process.");
            }
        }

        var readers = new[] { outputTask, errorTask }.Where(static task => task is not null).Cast<Task>().ToArray();
        if (readers.Length == 0)
        {
            return;
        }

        using var readerDrain = new CancellationTokenSource(ShellProbeDrainTimeout);
        try
        {
            await Task.WhenAll(readers).WaitAsync(readerDrain.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            logger.LogDebug(exception, "Failed to drain login-shell PATH probe output.");
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
    IReadOnlyDictionary<string, string?> EnvironmentVariables);
