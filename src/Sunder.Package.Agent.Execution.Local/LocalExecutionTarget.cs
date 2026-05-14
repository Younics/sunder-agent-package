using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionTarget(IPackageContext packageContext, LocalExecutionWorkspaceConfigService configService, LocalShellCatalogService shellCatalogService)
    : IAgentProcessExecutionTarget, IAgentWorkspaceBindingContributor, IAgentExecutionScopeProvider, IAgentExecutionResourceResolver
{
    private const int DefaultTimeoutSeconds = 300;
    private const int MaxOutputLength = 51200;
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[[0-?]*[ -/]*[@-~]|\x1B\][^\a]*(?:\a|\x1B\\)|\x1B[@-_]", RegexOptions.Compiled);

    public AgentExecutionTargetDescriptor Descriptor { get; } = new(
        "local",
        "local",
        "Local Machine",
        "Executes commands and file operations on this machine within package-configured workspace roots.",
        SupportsShell: true,
        SupportsFiles: true,
        SupportsSearch: true);

    AgentWorkspaceBindingDescriptor IAgentWorkspaceBindingContributor.Descriptor { get; } = new(
        "sunder.package.agent:execution-targets",
        "local",
        "primary-execution-target",
        "Local Machine",
        "Run shell and file tools on this machine using configured local roots.");

    public ValueTask<AgentWorkspaceBindingReadiness> GetReadinessAsync(
        AgentWorkspaceBindingContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var readiness = GetReadinessCore(context.Binding);
        return ValueTask.FromResult(new AgentWorkspaceBindingReadiness(context.Binding.BindingId, readiness.Status, readiness.Message));
    }

    public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(GetReadinessCore(context.Binding));
    }

    public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = configService.GetConfig(context.Binding.BindingId);
        var shell = shellCatalogService.ResolveShell(config.SelectedShellId);
        return ValueTask.FromResult(new AgentExecutionShellDescriptor(
            shell.ShellId,
            shell.DisplayName,
            shell.ExecutablePath,
            shell.SyntaxKind,
            BuildShellDescription(shell)));
    }

    public ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = configService.GetConfig(context.Binding.BindingId);
        return ValueTask.FromResult(new AgentExecutionScopeDescriptor(
            Descriptor.DisplayName,
            config.AllowedRoots,
            config.DefaultWorkingDirectory,
            "Local filesystem paths for this machine. On Windows, use the exact configured drive and user profile paths shown here."));
    }

    public ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        AgentExecutionTargetContext context,
        IReadOnlyList<AgentExecutionResourceDescriptor> resources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<IReadOnlyList<AgentResolvedExecutionResource>>(resources
            .Where(resource => !string.IsNullOrWhiteSpace(resource.HostPath))
            .Select(resource => new AgentResolvedExecutionResource(
                resource.ResourceId,
                resource.ResourceKind,
                resource.SourceId,
                resource.DisplayName,
                resource.HostPath,
                resource.HostPath,
                resource.AccessMode,
                resource.Metadata))
            .ToArray());
    }

    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = configService.GetConfig(context.Binding.BindingId);
        var resolved = ResolvePath(config, path, allowOutsideConfiguredScope: true);
        var boundary = IsInsideAllowedRoot(config, resolved)
            ? AgentPermissionBoundaryIds.ConfiguredScope
            : AgentPermissionBoundaryIds.OutsideConfiguredScope;
        return ValueTask.FromResult(new AgentResolvedResource(
            "file",
            resolved,
            resolved,
            boundary,
            File.Exists(resolved) || Directory.Exists(resolved)));
    }

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Command))
        {
            return new AgentShellCommandResult(1, "Command cannot be empty.");
        }

        var config = configService.GetConfig(context.Binding.BindingId);
        var shell = shellCatalogService.ResolveShell(config.SelectedShellId);
        var workingDirectory = ResolveWorkingDirectory(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var startInfo = BuildShellStartInfo(shell, request.Command, workingDirectory);
        return await ExecuteProcessStartInfoAsync(startInfo, request.TimeoutSeconds ?? ResolveDefaultTimeoutSeconds(), workingDirectory, cancellationToken);
    }

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.FileName))
        {
            return new AgentShellCommandResult(1, "Command file name cannot be empty.");
        }

        var config = configService.GetConfig(context.Binding.BindingId);
        var workingDirectory = ResolveWorkingDirectory(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var startInfo = new ProcessStartInfo
        {
            FileName = request.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };
        foreach (var argument in request.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        return await ExecuteProcessStartInfoAsync(startInfo, request.TimeoutSeconds ?? ResolveDefaultTimeoutSeconds(), workingDirectory, cancellationToken);
    }

    private static async ValueTask<AgentShellCommandResult> ExecuteProcessStartInfoAsync(
        ProcessStartInfo startInfo,
        int timeoutSeconds,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new AgentShellCommandResult(127, ex.Message, TimedOut: false, WorkingDirectory: workingDirectory);
        }

        var stdoutTask = ReadToEndBoundedAsync(process.StandardOutput, MaxOutputLength, cancellationToken);
        var stderrTask = ReadToEndBoundedAsync(process.StandardError, MaxOutputLength, cancellationToken);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new AgentShellCommandResult(124, $"Command timed out after {timeoutSeconds} seconds.", TimedOut: true, WorkingDirectory: workingDirectory);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = StripAnsiEscapeSequences(string.Concat(stdout.Content, stderr.Content));
        var truncatedOutput = TruncateOutput(output, out var wasTruncated);
        return new AgentShellCommandResult(
            process.ExitCode,
            truncatedOutput,
            TimedOut: false,
            WorkingDirectory: workingDirectory,
            WasTruncated: stdout.WasTruncated || stderr.WasTruncated || wasTruncated);
    }

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = configService.GetConfig(context.Binding.BindingId);
        var path = ResolvePath(config, request.Path, context.AllowOutsideConfiguredScope);
        if (Directory.Exists(path))
        {
            var entries = Directory.EnumerateFileSystemEntries(path)
                .Select(entry => Directory.Exists(entry) ? Path.GetFileName(entry) + Path.DirectorySeparatorChar : Path.GetFileName(entry))
                .OrderBy(entry => entry, StringComparer.OrdinalIgnoreCase);
            return new AgentFileReadResult(path, string.Join(Environment.NewLine, entries), IsDirectory: true);
        }

        if (!File.Exists(path))
        {
            return new AgentFileReadResult(path, $"File not found: {path}");
        }

        if (await IsBinaryFileAsync(path, cancellationToken))
        {
            throw new InvalidOperationException($"Binary file reads are not supported: {path}");
        }

        return new AgentFileReadResult(path, await File.ReadAllTextAsync(path, cancellationToken));
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = configService.GetConfig(context.Binding.BindingId);
        var path = ResolvePath(config, request.Path, context.AllowOutsideConfiguredScope);
        if (!request.Overwrite && File.Exists(path))
        {
            return new AgentFileMutationResult(path, "File already exists.", IsError: true, ErrorCode: "file-exists");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ResolveRoot(config));
        await File.WriteAllTextAsync(path, request.Content, cancellationToken);
        return new AgentFileMutationResult(path, $"Wrote {request.Content.Length} character(s).");
    }

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = configService.GetConfig(context.Binding.BindingId);
        var path = ResolvePath(config, request.Path, context.AllowOutsideConfiguredScope);
        if (File.Exists(path))
        {
            File.Delete(path);
            return ValueTask.FromResult(new AgentFileMutationResult(path, "File deleted."));
        }

        if (Directory.Exists(path))
        {
            Directory.Delete(path, request.Recursive);
            return ValueTask.FromResult(new AgentFileMutationResult(path, "Directory deleted."));
        }

        return ValueTask.FromResult(new AgentFileMutationResult(path, "Path does not exist.", IsError: true, ErrorCode: "path-not-found"));
    }

    private AgentExecutionTargetReadiness GetReadinessCore(AgentWorkspaceBindingRecord binding)
    {
        var config = configService.GetConfig(binding.BindingId);
        if (config.AllowedRoots.Count == 0)
        {
            return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure at least one local allowed root before using local execution.");
        }

        var missingRoots = config.AllowedRoots.Where(root => !Directory.Exists(root)).ToArray();
        if (missingRoots.Length > 0)
        {
            return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Failed, $"Allowed root does not exist: {missingRoots[0]}");
        }

        return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Local execution is ready.");
    }

    internal string ResolvePath(LocalExecutionWorkspaceConfig config, string path, bool allowOutsideConfiguredScope)
        => ResolvePathFromBase(config, path, ResolveDefaultBaseDirectory(config), allowOutsideConfiguredScope);

    private string ResolveWorkingDirectory(LocalExecutionWorkspaceConfig config, string? requestedWorkingDirectory, bool allowOutsideConfiguredScope)
        => string.IsNullOrWhiteSpace(requestedWorkingDirectory)
            ? ResolveDefaultBaseDirectory(config)
            : ResolvePathFromBase(config, requestedWorkingDirectory, ResolveDefaultBaseDirectory(config), allowOutsideConfiguredScope);

    private static string ResolveDefaultBaseDirectory(LocalExecutionWorkspaceConfig config)
        => string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? ResolveRoot(config)
            : config.DefaultWorkingDirectory;

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

    private static string ResolveRoot(LocalExecutionWorkspaceConfig config)
        => config.AllowedRoots.Count == 0
            ? throw new InvalidOperationException("The local execution binding has no allowed roots configured.")
            : config.AllowedRoots[0];

    private static bool IsInsideAllowedRoot(LocalExecutionWorkspaceConfig config, string candidate)
        => config.AllowedRoots.Any(root => LocalExecutionWorkspaceConfigService.IsSameOrChildPath(candidate, root));

    private static async Task<bool> IsBinaryFileAsync(string path, CancellationToken cancellationToken)
    {
        var buffer = new byte[Math.Min(8192, (int)Math.Min(new FileInfo(path).Length, 8192))];
        if (buffer.Length == 0)
        {
            return false;
        }

        await using var stream = File.OpenRead(path);
        var read = await stream.ReadAsync(buffer, cancellationToken);
        return buffer.Take(read).Any(value => value == 0);
    }

    private static string TruncateOutput(string output, out bool wasTruncated)
    {
        wasTruncated = output.Length > MaxOutputLength;
        return wasTruncated
            ? output[..MaxOutputLength] + Environment.NewLine + "[output truncated]"
            : output;
    }

    private static async Task<BoundedProcessOutput> ReadToEndBoundedAsync(StreamReader reader, int maxLength, CancellationToken cancellationToken)
    {
        var buffer = new char[4096];
        var builder = new StringBuilder(capacity: Math.Min(maxLength, buffer.Length));
        var wasTruncated = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read == 0)
            {
                break;
            }

            var remaining = maxLength - builder.Length;
            if (remaining > 0)
            {
                builder.Append(buffer, 0, Math.Min(read, remaining));
            }

            if (read > remaining)
            {
                wasTruncated = true;
            }
        }

        return new BoundedProcessOutput(builder.ToString(), wasTruncated);
    }

    private static string StripAnsiEscapeSequences(string output)
        => string.IsNullOrEmpty(output) ? output : AnsiEscapeRegex.Replace(output, string.Empty);

    private sealed record BoundedProcessOutput(string Content, bool WasTruncated);

    private static ProcessStartInfo BuildShellStartInfo(LocalShellDefinition shell, string command, string workingDirectory)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = shell.ExecutablePath,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = workingDirectory,
        };

        switch (shell.SyntaxKind)
        {
            case AgentShellSyntaxKinds.PowerShell:
                startInfo.ArgumentList.Add("-NoLogo");
                startInfo.ArgumentList.Add("-NoProfile");
                startInfo.ArgumentList.Add("-NonInteractive");
                startInfo.ArgumentList.Add("-ExecutionPolicy");
                startInfo.ArgumentList.Add("Bypass");
                startInfo.ArgumentList.Add("-Command");
                startInfo.ArgumentList.Add(command);
                break;

            case AgentShellSyntaxKinds.Cmd:
                startInfo.ArgumentList.Add("/d");
                startInfo.ArgumentList.Add("/s");
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(command);
                break;

            case AgentShellSyntaxKinds.PosixSh:
                startInfo.ArgumentList.Add("-c");
                startInfo.ArgumentList.Add(command);
                break;

            default:
                startInfo.ArgumentList.Add(command);
                break;
        }

        return startInfo;
    }

    private static string BuildShellDescription(LocalShellDefinition shell)
        => shell.SyntaxKind switch
        {
            AgentShellSyntaxKinds.PowerShell => $"Run PowerShell commands with {shell.DisplayName} on the local machine. Use PowerShell syntax such as Get-ChildItem, $HOME, and Join-Path.",
            AgentShellSyntaxKinds.Cmd => $"Run Windows Command Prompt commands with {shell.DisplayName} on the local machine. Use cmd.exe syntax such as dir and %USERPROFILE%.",
            AgentShellSyntaxKinds.PosixSh => $"Run POSIX shell commands with {shell.DisplayName} on the local machine. Use sh-compatible syntax.",
            _ => $"Run commands with custom shell {shell.DisplayName}. Follow its configured syntax kind.",
        };

    private int ResolveDefaultTimeoutSeconds()
        => int.TryParse(packageContext.Configuration.GetValue("shell.timeoutSeconds.default"), out var parsed) && parsed > 0
            ? parsed
            : DefaultTimeoutSeconds;

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }
}
