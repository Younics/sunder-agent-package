using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerExecutionTarget(
    IPackageContext packageContext,
    DockerExecutionWorkspaceConfigService configService,
    DockerContainerLifecycleService lifecycleService)
    : IAgentProcessExecutionTarget, IAgentWorkspaceBindingContributor, IAgentExecutionScopeProvider
{
    private const int DefaultTimeoutSeconds = 300;
    private const int MaxOutputLength = 51200;

    internal static Func<IReadOnlyList<string>, int, CancellationToken, string?, Task<DockerProcessResult>>? RunDockerOverride { get; set; }

    public AgentExecutionTargetDescriptor Descriptor { get; } = new(
        "docker",
        "docker",
        "Docker Container",
        "Creates or reuses a Docker container from a workspace image and runs tools inside configured container roots.",
        SupportsShell: true,
        SupportsFiles: true,
        SupportsSearch: true);

    AgentWorkspaceBindingDescriptor IAgentWorkspaceBindingContributor.Descriptor { get; } = new(
        "sunder.package.agent:execution-targets",
        "docker",
        "primary-execution-target",
        "Docker Container",
        "Run shell and file tools inside a Docker container created from the workspace image.");

    public async ValueTask<AgentWorkspaceBindingReadiness> GetReadinessAsync(
        AgentWorkspaceBindingContext context,
        CancellationToken cancellationToken = default)
    {
        var readiness = await GetReadinessAsync(new AgentExecutionTargetContext(null, null, context.Workspace, context.Binding), cancellationToken);
        return new AgentWorkspaceBindingReadiness(context.Binding.BindingId, readiness.Status, readiness.Message);
    }

    public async ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = configService.GetConfig(context.Binding.BindingId);
            if (string.IsNullOrWhiteSpace(config.ImageReference))
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure a Docker image before using Docker execution.");
            }

            if (config.AllowedRoots.Count == 0)
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure at least one Docker allowed root before using Docker execution.");
            }

            var imageReadiness = await new DockerImageCatalogService(packageContext)
                .GetReadinessAsync(config.ImageReference, cancellationToken)
                .ConfigureAwait(false);
            if (!imageReadiness.IsReady)
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Failed, imageReadiness.Message);
            }

            using var lease = await AcquireContainerAsync(context, config, cancellationToken);
            var shellValidation = await ValidateShellAsync(lease.ContainerName, ResolveShellPath(config), cancellationToken);
            return shellValidation.ExitCode == 0
                ? new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Docker execution is ready.")
                : new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Failed, $"Docker shell is unavailable at '{ResolveShellPath(config)}': {shellValidation.Output}".Trim());
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Failed, ex.Message);
        }
    }

    public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = configService.GetConfig(context.Binding.BindingId);
        var shellPath = ResolveShellPath(config);
        var displayName = ResolveShellDisplayName(shellPath);
        return ValueTask.FromResult(new AgentExecutionShellDescriptor(
            displayName.ToLowerInvariant().Replace(' ', '-'),
            displayName,
            shellPath,
            AgentShellSyntaxKinds.PosixSh,
            $"Run POSIX commands with {shellPath} inside the selected Docker container. Use Linux/POSIX shell syntax."));
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
            ResolveDefaultBaseDirectory(config),
            "Container filesystem paths. Use POSIX-style absolute paths inside the selected Docker container."));
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
        return ValueTask.FromResult(new AgentResolvedResource("file", resolved, resolved, boundary, Exists: true));
    }

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = configService.GetConfig(context.Binding.BindingId);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? ResolveDefaultBaseDirectory(config)
            : ResolvePath(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var result = await RunDockerAsync(["exec", "-w", workingDirectory, lease.ContainerName, ResolveShellPath(config), "-c", request.Command], request.TimeoutSeconds ?? ResolveDefaultTimeoutSeconds(), cancellationToken);
        return new AgentShellCommandResult(result.ExitCode, result.Output, result.TimedOut, workingDirectory, result.WasTruncated);
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
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        var workingDirectory = string.IsNullOrWhiteSpace(request.WorkingDirectory)
            ? ResolveDefaultBaseDirectory(config)
            : ResolvePath(config, request.WorkingDirectory, context.AllowOutsideConfiguredScope);
        var dockerArgs = new List<string> { "exec", "-w", workingDirectory, lease.ContainerName, request.FileName };
        dockerArgs.AddRange(request.Arguments);

        var result = await RunDockerAsync(dockerArgs, request.TimeoutSeconds ?? ResolveDefaultTimeoutSeconds(), cancellationToken);
        return new AgentShellCommandResult(result.ExitCode, result.Output, result.TimedOut, workingDirectory, result.WasTruncated);
    }

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = configService.GetConfig(context.Binding.BindingId);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        var path = ResolvePath(config, request.Path, context.AllowOutsideConfiguredScope);
        const string directoryMarker = "__SUNDER_DIRECTORY__";
        var command = $"if [ -d {Quote(path)} ]; then printf '%s\\n' {Quote(directoryMarker)}; ls -1A {Quote(path)}; else cat {Quote(path)}; fi";
        var result = await RunDockerAsync(["exec", lease.ContainerName, ResolveShellPath(config), "-c", command], ResolveDefaultTimeoutSeconds(), cancellationToken);
        var isDirectory = result.Output.StartsWith(directoryMarker, StringComparison.Ordinal);
        var output = isDirectory
            ? result.Output[directoryMarker.Length..].TrimStart('\r', '\n')
            : result.Output;
        return new AgentFileReadResult(path, output, isDirectory, result.WasTruncated);
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = configService.GetConfig(context.Binding.BindingId);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        var path = ResolvePath(config, request.Path, context.AllowOutsideConfiguredScope);
        var overwriteGuard = request.Overwrite ? string.Empty : $"if [ -e {Quote(path)} ]; then exit 73; fi && ";
        var command = $"{overwriteGuard}mkdir -p {Quote(GetDirectoryName(path))} && cat > {Quote(path)}";
        var result = await RunDockerAsync(["exec", "-i", lease.ContainerName, ResolveShellPath(config), "-c", command], ResolveDefaultTimeoutSeconds(), cancellationToken, request.Content);
        if (result.ExitCode == 73)
        {
            return new AgentFileMutationResult(path, "File already exists.", IsError: true, ErrorCode: "file-exists");
        }

        return result.ExitCode == 0
            ? new AgentFileMutationResult(path, $"Wrote {request.Content.Length} character(s).")
            : new AgentFileMutationResult(path, result.Output, IsError: true, ErrorCode: "docker-write-failed");
    }

    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = configService.GetConfig(context.Binding.BindingId);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        var path = ResolvePath(config, request.Path, context.AllowOutsideConfiguredScope);
        var command = request.Recursive ? $"rm -rf {Quote(path)}" : $"rm -f {Quote(path)}";
        var result = await RunDockerAsync(["exec", lease.ContainerName, ResolveShellPath(config), "-c", command], ResolveDefaultTimeoutSeconds(), cancellationToken);
        return result.ExitCode == 0
            ? new AgentFileMutationResult(path, "Path deleted.")
            : new AgentFileMutationResult(path, result.Output, IsError: true, ErrorCode: "docker-delete-failed");
    }

    private Task<DockerContainerLifecycleService.DockerContainerLease> AcquireContainerAsync(
        AgentExecutionTargetContext context,
        DockerExecutionWorkspaceConfig config,
        CancellationToken cancellationToken)
    {
        var container = ResolveContainerName(config, context.Binding.BindingId);
        return lifecycleService.AcquireAsync(
            container,
            async ct => await EnsureContainerAsync(context, config, ct)
                        ?? throw new InvalidOperationException("Docker container is unavailable."),
            StopContainerAsync,
            cancellationToken);
    }

    private async Task<string?> EnsureContainerAsync(AgentExecutionTargetContext context, DockerExecutionWorkspaceConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = ResolveContainerName(config, context.Binding.BindingId);
        configService.EnsureHostRoots(config);
        var mounts = configService.ResolveMounts(config);
        var signature = BuildContainerSignature(config, mounts);
        var inspect = await RunDockerAsync(["inspect", "-f", "{{.State.Running}} {{ index .Config.Labels \"sunder.resources.signature\" }}", container], ResolveDefaultTimeoutSeconds(), cancellationToken);
        var existing = ParseInspectResult(inspect.Output);
        if (inspect.ExitCode == 0 && existing.Running)
        {
            if (string.Equals(existing.Signature, signature, StringComparison.Ordinal))
            {
                return container;
            }

            await RunDockerAsync(["rm", "-f", container], ResolveDefaultTimeoutSeconds(), cancellationToken);
        }
        else if (inspect.ExitCode == 0)
        {
            if (string.Equals(existing.Signature, signature, StringComparison.Ordinal))
            {
                var start = await RunDockerAsync(["start", container], ResolveDefaultTimeoutSeconds(), cancellationToken);
                if (start.ExitCode == 0)
                {
                    return container;
                }

                throw new InvalidOperationException(FormatDockerContainerStartFailure(container, start.Output));
            }

            await RunDockerAsync(["rm", "-f", container], ResolveDefaultTimeoutSeconds(), cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(config.ImageReference))
        {
            throw new InvalidOperationException("Configure a Docker image before using Docker execution.");
        }

        var image = config.ImageReference;
        var root = ResolveDefaultBaseDirectory(config);
        var args = new List<string> { "run", "--pull", "never", "-d", "--name", container, "--label", $"sunder.resources.signature={signature}", "-w", root };
        AddNonInteractiveEnvironment(args);
        foreach (var mount in mounts)
        {
            args.Add("--mount");
            args.Add(string.Concat(
                "type=bind,source=", mount.HostPath,
                ",target=", mount.ContainerPath));
        }

        args.Add(image);
        args.Add("tail");
        args.Add("-f");
        args.Add("/dev/null");
        var run = await RunDockerAsync(args, ResolveDefaultTimeoutSeconds(), cancellationToken);
        if (run.ExitCode == 0)
        {
            return container;
        }

        throw new InvalidOperationException(FormatDockerContainerRunFailure(container, image, run.Output));
    }

    private static string FormatDockerContainerStartFailure(string containerName, string output)
        => AppendDockerOutput($"Docker container '{containerName}' failed to start.", output);

    private static string FormatDockerContainerRunFailure(string containerName, string imageReference, string output)
        => AppendDockerOutput($"Docker container '{containerName}' failed to start from image '{imageReference}'.", output);

    private static string AppendDockerOutput(string message, string output)
    {
        var trimmed = output.Trim();
        return string.IsNullOrWhiteSpace(trimmed)
            ? message
            : $"{message} {trimmed}";
    }

    private async Task StopContainerAsync(string containerName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(containerName))
        {
            return;
        }

        await RunDockerAsync(["stop", containerName], ResolveDefaultTimeoutSeconds(), cancellationToken);
    }

    private async Task<DockerProcessResult> ValidateShellAsync(string containerName, string shellPath, CancellationToken cancellationToken)
        => await RunDockerAsync(["exec", containerName, shellPath, "-c", "printf ready"], ResolveDefaultTimeoutSeconds(), cancellationToken);

    private static void AddNonInteractiveEnvironment(List<string> args)
    {
        foreach (var value in new[]
                 {
                     "DEBIAN_FRONTEND=noninteractive",
                     "GIT_TERMINAL_PROMPT=0",
                     "GIT_EDITOR=:",
                     "CI=true",
                     "PIP_NO_INPUT=1",
                     "NPM_CONFIG_YES=true",
                 })
        {
            args.Add("--env");
            args.Add(value);
        }
    }

    private static (bool Running, string? Signature) ParseInspectResult(string output)
    {
        var normalized = output.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return (false, null);
        }

        var parts = normalized.Split(' ', 2, StringSplitOptions.TrimEntries);
        return (
            parts.Length > 0 && string.Equals(parts[0], "true", StringComparison.OrdinalIgnoreCase),
            parts.Length > 1 && !string.Equals(parts[1], "<no value>", StringComparison.OrdinalIgnoreCase) ? parts[1] : null);
    }

    private static string ResolveContainerName(DockerExecutionWorkspaceConfig config, string bindingId)
        => string.IsNullOrWhiteSpace(config.ContainerName)
            ? DockerExecutionWorkspaceConfigService.DefaultContainerName
            : config.ContainerName;

    private static string ResolveShellPath(DockerExecutionWorkspaceConfig config)
        => string.IsNullOrWhiteSpace(config.ShellPath)
            ? DockerExecutionWorkspaceConfigService.DefaultShellPath
            : config.ShellPath;

    private static string ResolveShellDisplayName(string shellPath)
    {
        var shellName = shellPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return string.IsNullOrWhiteSpace(shellName) ? "POSIX shell" : shellName switch
        {
            "bash" => "Bash",
            "sh" => "POSIX sh",
            "zsh" => "Zsh",
            _ => shellName,
        };
    }

    private static string BuildContainerSignature(
        DockerExecutionWorkspaceConfig config,
        IReadOnlyList<DockerExecutionMount> mounts)
    {
        var builder = new StringBuilder();
        builder.AppendLine(config.ImageReference ?? string.Empty)
            .AppendLine(config.DefaultWorkingDirectory ?? string.Empty)
            .AppendLine(config.ShellPath ?? string.Empty);
        foreach (var root in config.AllowedRoots.OrderBy(root => root, StringComparer.Ordinal))
        {
            builder.Append("root:").AppendLine(root);
        }

        foreach (var mount in mounts.OrderBy(mount => mount.ContainerPath, StringComparer.Ordinal))
        {
            builder.Append("mount:")
                .Append(mount.HostPath).Append('|')
                .Append(mount.ContainerPath).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static string ResolveDefaultBaseDirectory(DockerExecutionWorkspaceConfig config)
        => string.IsNullOrWhiteSpace(config.DefaultWorkingDirectory)
            ? config.AllowedRoots.FirstOrDefault() ?? "/workspace"
            : config.DefaultWorkingDirectory;

    private static string ResolvePath(DockerExecutionWorkspaceConfig config, string path, bool allowOutsideConfiguredScope)
    {
        var normalized = ResolveRuntimePath(path, ResolveDefaultBaseDirectory(config));

        if (!allowOutsideConfiguredScope && !IsInsideAllowedRoot(config, normalized))
        {
            throw new InvalidOperationException($"Path '{path}' is outside the Docker workspace allowed roots.");
        }

        return normalized;
    }

    private static string ResolveRuntimePath(string path, string baseDirectory)
    {
        var candidate = string.IsNullOrWhiteSpace(path)
            ? baseDirectory
            : path.Trim().Replace('\\', '/');
        if (!candidate.StartsWith("/", StringComparison.Ordinal))
        {
            candidate = DockerExecutionWorkspaceConfigService.NormalizeContainerPath(baseDirectory) + "/" + candidate;
        }

        var segments = new List<string>();
        foreach (var segment in candidate.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (segment)
            {
                case ".":
                    continue;
                case ".." when segments.Count > 0:
                    segments.RemoveAt(segments.Count - 1);
                    continue;
                case "..":
                    throw new InvalidOperationException($"Path '{path}' cannot resolve above the container root.");
                default:
                    segments.Add(segment);
                    break;
            }
        }

        return DockerExecutionWorkspaceConfigService.NormalizeContainerPath("/" + string.Join("/", segments));
    }

    private static bool IsInsideAllowedRoot(DockerExecutionWorkspaceConfig config, string candidate)
        => config.AllowedRoots.Any(root => DockerExecutionWorkspaceConfigService.IsSameOrChildPath(candidate, root));

    private int ResolveDefaultTimeoutSeconds()
        => int.TryParse(packageContext.Configuration.GetValue("docker.timeoutSeconds.default"), out var parsed) && parsed > 0
            ? parsed
            : DefaultTimeoutSeconds;

    private static string Quote(string value) => "'" + value.Replace("'", "'\\''") + "'";

    private static string GetDirectoryName(string path)
    {
        var index = path.LastIndexOf('/');
        return index <= 0 ? "/" : path[..index];
    }

    private async Task<DockerProcessResult> RunDockerAsync(IReadOnlyList<string> args, int timeoutSeconds, CancellationToken cancellationToken, string? standardInput = null)
    {
        if (RunDockerOverride is { } overrideRunner)
        {
            return await overrideRunner(args, timeoutSeconds, cancellationToken, standardInput);
        }

        ProcessStartInfo startInfo;
        try
        {
            startInfo = CreateDockerStartInfo(packageContext, args, standardInput);
        }
        catch (Exception ex)
        {
            return new DockerProcessResult(127, $"Failed to start Docker CLI: {FormatDockerStartError(ex)}", TimedOut: false, WasTruncated: false);
        }

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new DockerProcessResult(127, $"Failed to start Docker CLI: {FormatDockerStartError(ex)}", TimedOut: false, WasTruncated: false);
        }

        var stdoutTask = ReadToEndBoundedAsync(process.StandardOutput, MaxOutputLength, cancellationToken);
        var stderrTask = ReadToEndBoundedAsync(process.StandardError, MaxOutputLength, cancellationToken);
        var stdinTask = WriteStandardInputAsync(process, standardInput, cancellationToken);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            await stdinTask;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            return new DockerProcessResult(124, $"Docker command timed out after {timeoutSeconds} seconds.", TimedOut: true, WasTruncated: false);
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        var output = string.Concat(stdout.Content, stderr.Content);
        var truncatedOutput = TruncateOutput(output, out var wasTruncated);
        return new DockerProcessResult(process.ExitCode, truncatedOutput, TimedOut: false, stdout.WasTruncated || stderr.WasTruncated || wasTruncated);
    }

    internal static ProcessStartInfo CreateDockerStartInfo(
        IPackageContext packageContext,
        IReadOnlyList<string> args,
        string? standardInput)
        => DockerCli.CreateStartInfo(packageContext, args, redirectStandardInput: standardInput is not null);

    private static async Task WriteStandardInputAsync(Process process, string? standardInput, CancellationToken cancellationToken)
    {
        if (standardInput is null)
        {
            return;
        }

        try
        {
            await process.StandardInput.WriteAsync(standardInput.AsMemory(), cancellationToken);
            await process.StandardInput.FlushAsync(cancellationToken);
        }
        catch (Exception ex) when ((ex is IOException or InvalidOperationException) && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            try
            {
                process.StandardInput.Close();
            }
            catch
            {
            }
        }
    }

    private static string FormatDockerStartError(Exception exception)
        => exception.Message.Contains("filename or extension is too long", StringComparison.OrdinalIgnoreCase)
            ? "the generated command line was too long. File content should be streamed through stdin instead of passed as a Docker CLI argument."
            : exception.Message;

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

    internal sealed record DockerProcessResult(int ExitCode, string Output, bool TimedOut, bool WasTruncated);

    private sealed record BoundedProcessOutput(string Content, bool WasTruncated);
}
