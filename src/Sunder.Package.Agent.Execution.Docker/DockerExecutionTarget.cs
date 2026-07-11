using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerExecutionTarget
    : IAgentProcessExecutionTarget, IAgentRangedFileExecutionTarget, IAgentWorkspaceBindingContributor, IAgentExecutionScopeProvider, IAgentExecutionPathMapper, IAgentExecutionPathEnvironment
{
    private readonly DockerExecutionWorkspaceConfigService _configService;
    private readonly DockerContainerLifecycleService _lifecycleService;
    private readonly DockerImageCatalogService _imageCatalogService;
    private readonly DockerCommandRunner _commandRunner;
    private readonly DockerFileSystemExecutor _fileSystemExecutor;

    public DockerExecutionTarget(
        IPackageContext packageContext,
        DockerExecutionWorkspaceConfigService configService,
        DockerContainerLifecycleService lifecycleService,
        DockerImageCatalogService? imageCatalogService = null,
        DockerCliRunner? dockerCliRunner = null)
    {
        var runner = dockerCliRunner ?? new DockerCliRunner(packageContext);
        _configService = configService;
        _lifecycleService = lifecycleService;
        _imageCatalogService = imageCatalogService ?? new DockerImageCatalogService(packageContext, runner);
        _commandRunner = new DockerCommandRunner(packageContext, runner);
        _fileSystemExecutor = new DockerFileSystemExecutor(_commandRunner);
    }

    public AgentExecutionTargetDescriptor Descriptor { get; } = new(
        "docker",
        "docker",
        "Docker Container",
        "Creates or reuses a Docker container from a workspace image and mounts configured workspace paths.",
        SupportsShell: true,
        SupportsFiles: true,
        SupportsSearch: true);

    AgentWorkspaceBindingDescriptor IAgentWorkspaceBindingContributor.Descriptor { get; } = new(
        "sunder.package.agent:execution-targets",
        "docker",
        "primary-execution-target",
        "Docker Container",
        "Run shell and file tools inside a Docker container with configured workspace paths mounted.");

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
            var config = BuildRuntimeConfig(context);
            if (string.IsNullOrWhiteSpace(config.ImageReference))
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure a Docker image before using Docker execution.");
            }

            if (config.Mounts.Count == 0)
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure at least one workspace path before using Docker execution.");
            }

            var missingRoots = config.Mounts.Where(mount => !Directory.Exists(mount.HostPath)).ToArray();
            if (missingRoots.Length > 0)
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Failed, $"Workspace path does not exist: {missingRoots[0].HostPath}");
            }

            var imageReadiness = await _imageCatalogService.GetReadinessAsync(config.ImageReference, cancellationToken)
                .ConfigureAwait(false);
            if (!imageReadiness.IsReady)
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Failed, imageReadiness.Message);
            }

            using var lease = await AcquireContainerAsync(context, config, cancellationToken);
            var shellPath = DockerCommandRunner.ResolveShellPath(config);
            var shellValidation = await ValidateShellAsync(lease.ContainerName, shellPath, cancellationToken);
            return shellValidation.ExitCode == 0
                ? new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Ready, "Docker execution is ready.")
                : new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.Failed, $"Docker shell is unavailable at '{shellPath}': {shellValidation.Output}".Trim());
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
        var config = _configService.GetConfig(context.Binding.BindingId);
        return ValueTask.FromResult(DockerCommandRunner.GetShellDescriptor(config));
    }

    public ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = BuildRuntimeConfig(context);
        return ValueTask.FromResult(new AgentExecutionScopeDescriptor(
            Descriptor.DisplayName,
            config.Mounts.Select(mount => mount.ContainerPath).ToArray(),
            DockerPathResolver.ResolveDefaultBaseDirectory(config),
            "Container filesystem paths. Use POSIX-style absolute paths inside the selected Docker container."));
    }

    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = BuildRuntimeConfig(context);
        var resolved = DockerPathResolver.ResolveFileResource(
            config,
            path,
            allowOutsideConfiguredScope: true,
            exists: false);
        if (!DockerPathResolver.IsInsideWorkspacePath(config, resolved.CanonicalReference))
        {
            return ValueTask.FromResult(resolved);
        }

        var mapping = DockerPathResolver.MapToHostPath(config, resolved.CanonicalReference);
        return ValueTask.FromResult(resolved with
        {
            Exists = File.Exists(mapping.HostPath) || Directory.Exists(mapping.HostPath),
        });
    }

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = BuildRuntimeConfig(context);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        return await _commandRunner.ExecuteShellAsync(config, lease.ContainerName, context, request, cancellationToken);
    }

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = BuildRuntimeConfig(context);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        return await _commandRunner.ExecuteProcessAsync(config, lease.ContainerName, context, request, cancellationToken);
    }

    public ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = BuildRuntimeConfig(context);
        return ValueTask.FromResult(DockerPathResolver.MapToHostPath(config, executionPath));
    }

    public ValueTask<IReadOnlyList<string>> ListPathEntriesAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_configService.GetConfig(context.Binding.BindingId).PathEntries ?? []);
    }

    public ValueTask AddPathEntryAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executionPath))
        {
            return ValueTask.CompletedTask;
        }

        var config = _configService.GetConfig(context.Binding.BindingId);
        var pathEntry = DockerExecutionWorkspaceConfigService.NormalizeContainerPath(executionPath.Trim());
        var pathEntries = (config.PathEntries ?? [])
            .Append(pathEntry)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        _configService.SaveConfig(context.Binding.BindingId, config with { PathEntries = pathEntries });
        return ValueTask.CompletedTask;
    }

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = BuildRuntimeConfig(context);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        return await _fileSystemExecutor.ReadFileAsync(config, lease.ContainerName, request, context.AllowOutsideConfiguredScope, cancellationToken);
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = BuildRuntimeConfig(context);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        return await _fileSystemExecutor.WriteFileAsync(config, lease.ContainerName, request, context.AllowOutsideConfiguredScope, cancellationToken);
    }

    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = BuildRuntimeConfig(context);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        return await _fileSystemExecutor.DeleteFileAsync(config, lease.ContainerName, request, context.AllowOutsideConfiguredScope, cancellationToken);
    }

    private DockerExecutionRuntimeConfig BuildRuntimeConfig(AgentExecutionTargetContext context)
        => _configService.BuildRuntimeConfig(
            context.Binding.BindingId,
            context.Workspace,
            _configService.GetConfig(context.Binding.BindingId));

    private Task<DockerContainerLifecycleService.DockerContainerLease> AcquireContainerAsync(
        AgentExecutionTargetContext context,
        DockerExecutionRuntimeConfig config,
        CancellationToken cancellationToken)
    {
        var container = ResolveContainerName(config, context.Binding.BindingId);
        return _lifecycleService.AcquireAsync(
            container,
            async ct => await EnsureContainerAsync(context, config, ct)
                        ?? throw new InvalidOperationException("Docker container is unavailable."),
            StopContainerAsync,
            cancellationToken);
    }

    private async Task<string?> EnsureContainerAsync(AgentExecutionTargetContext context, DockerExecutionRuntimeConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = ResolveContainerName(config, context.Binding.BindingId);
        _configService.EnsureHostMountPaths(config);
        var mounts = _configService.ResolveMounts(config);
        var signature = BuildContainerSignature(config, mounts);
        var inspect = await RunDockerAsync(["inspect", "-f", "{{.State.Running}} {{ index .Config.Labels \"sunder.resources.signature\" }}", container], cancellationToken);
        var existing = ParseInspectResult(inspect.Output);
        if (inspect.ExitCode == 0 && existing.Running)
        {
            if (string.Equals(existing.Signature, signature, StringComparison.Ordinal))
            {
                return container;
            }

            await RunDockerAsync(["rm", "-f", container], cancellationToken);
        }
        else if (inspect.ExitCode == 0)
        {
            if (string.Equals(existing.Signature, signature, StringComparison.Ordinal))
            {
                var start = await RunDockerAsync(["start", container], cancellationToken);
                if (start.ExitCode == 0)
                {
                    return container;
                }

                throw new InvalidOperationException(FormatDockerContainerStartFailure(container, start.Output));
            }

            await RunDockerAsync(["rm", "-f", container], cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(config.ImageReference))
        {
            throw new InvalidOperationException("Configure a Docker image before using Docker execution.");
        }

        var image = config.ImageReference;
        var root = DockerPathResolver.ResolveDefaultBaseDirectory(config);
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
        var run = await RunDockerAsync(args, cancellationToken);
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

        await RunDockerAsync(["stop", containerName], cancellationToken);
    }

    private async Task<DockerCliRunResult> ValidateShellAsync(string containerName, string shellPath, CancellationToken cancellationToken)
        => await RunDockerAsync(["exec", containerName, shellPath, "-c", "printf ready"], cancellationToken);

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

    private static string ResolveContainerName(DockerExecutionRuntimeConfig config, string bindingId)
        => string.IsNullOrWhiteSpace(config.ContainerName)
            ? DockerExecutionWorkspaceConfigService.BuildContainerName(bindingId)
            : config.ContainerName;

    private static string BuildContainerSignature(
        DockerExecutionRuntimeConfig config,
        IReadOnlyList<DockerExecutionMount> mounts)
    {
        var builder = new StringBuilder();
        builder.AppendLine(config.ImageReference ?? string.Empty)
            .AppendLine(config.DefaultWorkingDirectory ?? string.Empty)
            .AppendLine(config.ShellPath ?? string.Empty);

        foreach (var mount in mounts.OrderBy(mount => mount.ContainerPath, StringComparer.Ordinal))
        {
            builder.Append("mount:")
                .Append(mount.HostPath).Append('|')
                .Append(mount.ContainerPath).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private async Task<DockerCliRunResult> RunDockerAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
        => await _commandRunner.RunAsync(args, _commandRunner.ResolveDefaultTimeoutSeconds(), cancellationToken);
}
