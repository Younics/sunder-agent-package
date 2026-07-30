using System.Security.Cryptography;
using System.Text;
using System.Collections.Concurrent;
using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionTarget
    : IAgentProcessExecutionTarget, IAgentStructuredFileSearchExecutionTarget, IAgentRangedFileExecutionTarget, IAgentExecutionScopeProvider, IAgentExecutionPathMapper, IAgentExecutionPathEnvironment, IAgentScopedInstructionDiscoveryTarget, IAgentResourceAuthorityExecutionTarget
{
    internal const string DefaultNetworkPolicy = "none";
    internal const string DefaultMemoryLimit = "2g";
    internal const string DefaultCpuLimit = "2";
    internal const string DefaultPidLimit = "256";
    private const string ContainerSecurityPolicyVersion = "4";

    private readonly DockerExecutionWorkspaceConfigService _configService;
    private readonly IPackageContext _packageContext;
    private readonly DockerContainerLifecycleService _lifecycleService;
    private readonly DockerImageCatalogService _imageCatalogService;
    private readonly DockerCommandRunner _commandRunner;
    private readonly DockerCliRunner _dockerCliRunner;
    private readonly DockerFileSystemExecutor _fileSystemExecutor;
    private readonly IDockerMountIdentityVerifier _mountIdentityVerifier;
    private readonly object _daemonIdentityLock = new();
    private string? _pinnedEndpointIdentity;
    private string? _verifiedDaemonIdentity;
    private readonly ConcurrentDictionary<string, string> _verifiedContainerSignatures = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _verifiedImageIdentities = new(StringComparer.Ordinal);

    public DockerExecutionTarget(
        IPackageContext packageContext,
        DockerExecutionWorkspaceConfigService configService,
        DockerContainerLifecycleService lifecycleService,
        DockerImageCatalogService? imageCatalogService = null,
        DockerCliRunner? dockerCliRunner = null)
        : this(
            packageContext,
            configService,
            lifecycleService,
            imageCatalogService,
            dockerCliRunner,
            new DockerMountIdentityVerifier())
    {
    }

    internal DockerExecutionTarget(
        IPackageContext packageContext,
        DockerExecutionWorkspaceConfigService configService,
        DockerContainerLifecycleService lifecycleService,
        DockerImageCatalogService? imageCatalogService,
        DockerCliRunner? dockerCliRunner,
        IDockerMountIdentityVerifier mountIdentityVerifier)
    {
        var runner = dockerCliRunner ?? new DockerCliRunner(packageContext);
        _packageContext = packageContext;
        _configService = configService;
        _lifecycleService = lifecycleService;
        _imageCatalogService = imageCatalogService ?? new DockerImageCatalogService(packageContext, runner);
        _dockerCliRunner = runner;
        _commandRunner = new DockerCommandRunner(packageContext, runner);
        _fileSystemExecutor = new DockerFileSystemExecutor();
        _mountIdentityVerifier = mountIdentityVerifier;
    }

    public AgentExecutionTargetDescriptor Descriptor { get; } = new(
        "docker",
        "docker",
        "Docker Container",
        "Creates a resource-bounded Docker container with networking disabled by default. Mounted workspace paths remain writable host data; this is not a complete security sandbox.",
        SupportsShell: true,
        SupportsFiles: true);

    public async ValueTask<string?> GetConfigurationGenerationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        var snapshot = await CaptureCurrentConfigurationAsync(
            context.Binding.BindingId,
            cancellationToken);
        return _configService.CreateGeneration(context.Binding.BindingId, snapshot);
    }

    public async ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var config = await BuildRuntimeConfigAsync(context, cancellationToken);
            using var configurationScope = _commandRunner.UseConfiguration(config);
            if (string.IsNullOrWhiteSpace(config.ImageReference))
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure a Docker image before using Docker execution.");
            }

            if (config.Mounts.Count == 0)
            {
                return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure at least one workspace path before using Docker execution.");
            }

            foreach (var mount in config.Mounts)
            {
                using var root = HostSecurePathEngine.OpenRoot(mount.HostPath, cancellationToken: cancellationToken);
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

    public async ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await GetCurrentConfigurationAsync(context, cancellationToken);
        return DockerCommandRunner.GetShellDescriptor(snapshot.WorkspaceConfig);
    }

    public async ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        return new AgentExecutionScopeDescriptor(
            Descriptor.DisplayName,
            config.Mounts.Select(mount => mount.ContainerPath).ToArray(),
            DockerPathResolver.ResolveDefaultBaseDirectory(config),
            "Container filesystem paths. Use POSIX-style absolute paths inside the selected Docker container.");
    }

    public async ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        var binding = DockerPathResolver.ResolveHostBinding(config, path);
        using var operation = await AcquireStructuredOperationAsync(context, config, cancellationToken);
        var retainedMountRoot = operation.TakeMountRoot(binding.Mount);
        LocalSecureApprovalLease? resourceAuthority = null;
        try
        {
            resourceAuthority = HostSecurePathEngine.CaptureFromRoot(
                retainedMountRoot,
                binding.HostPath,
                cancellationToken: cancellationToken,
                allowMissingSuffix: true);
            var identity = CaptureStructuredIdentity(config, context.Binding.BindingId, operation);
            return _fileSystemExecutor.ResolveFileResource(
                config,
                path,
                identity.NamespaceFingerprint,
                cancellationToken,
                resourceAuthority,
                identity.MountIdentityChains,
                context);
        }
        catch
        {
            if (resourceAuthority is null)
            {
                retainedMountRoot.Dispose();
            }
            else
            {
                resourceAuthority.Dispose();
            }
            throw;
        }
    }

    public ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var valid = context.ApprovedResourceClaims.Count <= 128
                    && context.ApprovedResourceClaims.All(claim =>
                        claim.ConfiguredRoot is not null
                        && claim.NamespaceId.StartsWith(
                            DockerFileSystemExecutor.ClaimNamespacePrefix,
                            StringComparison.Ordinal));
        return ValueTask.FromResult(valid
            ? new AgentResourceAuthorityValidation(true)
            : new AgentResourceAuthorityValidation(
                false,
                AgentToolResultErrorCodes.PermissionReapprovalRequired,
                "Docker structured resource claims are missing or invalid."));
    }

    public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
    {
    }

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        return await _commandRunner.ExecuteShellAsync(config, lease.ContainerName, context, request, cancellationToken);
    }

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        using var lease = await AcquireContainerAsync(context, config, cancellationToken);
        return await _commandRunner.ExecuteProcessAsync(config, lease.ContainerName, context, request, cancellationToken);
    }

    public async ValueTask<AgentFileSearchResult> ExecuteFileSearchAsync(
        AgentExecutionTargetContext context,
        AgentFileSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!HostSecureFileSearch.TryValidateRequest(request, out var validationError))
        {
            return AgentFileSearchResult.Failure(
                AgentFileSearchErrorCodes.InvalidRequest,
                validationError!);
        }
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        try
        {
            _ = DockerPathResolver.ResolveHostBinding(config, request.Path);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return AgentFileSearchResult.Failure(ex.ErrorCode, ex.Message);
        }
        using var operation = await AcquireStructuredOperationAsync(context, config, cancellationToken);
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            var identity = CaptureStructuredIdentity(config, context.Binding.BindingId, operation);
            var authority = CaptureConfiguredResourceAuthority(context, path, identity, operation, cancellationToken);
            return await _fileSystemExecutor.SearchAsync(config, request, authority, cancellationToken);
        }
        catch (LocalSecureApprovalChangedException ex)
        {
            return AgentFileSearchResult.Failure(AgentToolResultErrorCodes.PermissionReapprovalRequired, ex.Message);
        }
    }

    public async ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        _ = DockerPathResolver.ResolveHostBinding(config, executionPath);
        using var operation = await AcquireStructuredOperationAsync(context, config, cancellationToken);
        return DockerPathResolver.MapToHostPath(config, executionPath);
    }

    public async ValueTask<IReadOnlyList<string>> ListPathEntriesAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await GetCurrentConfigurationAsync(context, cancellationToken)).WorkspaceConfig.PathEntries ?? [];
    }

    public async ValueTask AddPathEntryAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(executionPath))
        {
            return;
        }

        var pathEntry = DockerExecutionWorkspaceConfigService.NormalizeContainerPath(executionPath.Trim());
        await _configService.UpdateConfigAsync(
            context.Binding.BindingId,
            async (config, token) =>
            {
                var snapshot = await CaptureCurrentConfigurationAsync(
                    context.Binding.BindingId,
                    token,
                    config);
                if (context.ExpectedConfigurationGeneration is { } expected
                    && !string.Equals(
                        expected,
                        _configService.CreateGeneration(context.Binding.BindingId, snapshot),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Docker execution configuration changed after permission planning; explicit reapproval is required.");
                }
                if (context.ExpectedConfigurationGeneration is not null
                    && snapshot.WorkspaceConfig.ImageReference is not null
                    && snapshot.ImageIdentity is null)
                {
                    throw new InvalidOperationException(
                        "The Docker image identity is not available for this approved configuration; explicit reapproval is required after target readiness completes.");
                }

                return config with
                {
                    PathEntries = (config.PathEntries ?? [])
                        .Append(pathEntry)
                        .Distinct(StringComparer.Ordinal)
                        .ToArray(),
                };
            },
            cancellationToken);
    }

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!FileOperation.TryValidateRange(request.Offset, request.Limit, out var rangeError))
        {
            return AgentFileReadResult.Failure(
                request.Path,
                AgentFileReadErrorCodes.InvalidRange,
                rangeError!);
        }
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        try
        {
            _ = DockerPathResolver.ResolveHostBinding(config, request.Path);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return AgentFileReadResult.Failure(request.Path, ex.ErrorCode, ex.Message);
        }
        using var operation = await AcquireStructuredOperationAsync(context, config, cancellationToken);
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            var identity = CaptureStructuredIdentity(config, context.Binding.BindingId, operation);
            var authority = CaptureConfiguredResourceAuthority(context, path, identity, operation, cancellationToken);
            return await _fileSystemExecutor.ReadFileAsync(
                config,
                request,
                authority,
                cancellationToken);
        }
        catch (LocalSecureApprovalChangedException ex)
        {
            return AgentFileReadResult.Failure(
                request.Path,
                AgentToolResultErrorCodes.PermissionReapprovalRequired,
                ex.Message);
        }
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        try
        {
            _ = DockerPathResolver.ResolveHostBinding(config, request.Path);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return FileOperation.Failure(request.Path, ex.Message, ex.ErrorCode);
        }
        using var operation = await AcquireStructuredOperationAsync(context, config, cancellationToken);
        LocalSecureApprovalLease? postMutationAuthority = null;
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            var identity = CaptureStructuredIdentity(config, context.Binding.BindingId, operation);
            var authority = CaptureConfiguredResourceAuthority(context, path, identity, operation, cancellationToken);
            var result = await _fileSystemExecutor.WriteFileAsync(
                config,
                request,
                authority,
                cancellationToken,
                postMutationAuthoritySink: context.CapturePostMutationResource
                    ? captured => postMutationAuthority = captured
                    : null);
            return AttachPostMutationResource(
                config,
                context,
                request.Path,
                identity,
                result,
                ref postMutationAuthority);
        }
        catch (LocalSecureApprovalChangedException ex)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentToolResultErrorCodes.PermissionReapprovalRequired);
        }
        finally
        {
            postMutationAuthority?.Dispose();
        }
    }

    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        try
        {
            _ = DockerPathResolver.ResolveHostBinding(config, request.Path);
        }
        catch (DockerStructuredBindRequiredException ex)
        {
            return FileOperation.Failure(request.Path, ex.Message, ex.ErrorCode);
        }
        using var operation = await AcquireStructuredOperationAsync(context, config, cancellationToken);
        LocalSecureApprovalLease? postMutationAuthority = null;
        try
        {
            var path = DockerPathResolver.ResolveHostBinding(config, request.Path);
            var identity = CaptureStructuredIdentity(config, context.Binding.BindingId, operation);
            var authority = CaptureConfiguredResourceAuthority(context, path, identity, operation, cancellationToken);
            var result = await _fileSystemExecutor.DeleteFileAsync(
                config,
                request,
                authority,
                identity.MountIdentityChains,
                cancellationToken,
                postMutationAuthoritySink: context.CapturePostMutationResource
                    ? captured => postMutationAuthority = captured
                    : null);
            return AttachPostMutationResource(
                config,
                context,
                request.Path,
                identity,
                result,
                ref postMutationAuthority);
        }
        catch (LocalSecureApprovalChangedException ex)
        {
            return FileOperation.Failure(
                request.Path,
                ex.Message,
                AgentToolResultErrorCodes.PermissionReapprovalRequired);
        }
        finally
        {
            postMutationAuthority?.Dispose();
        }
    }

    public async ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(
        AgentExecutionTargetContext context,
        AgentScopedInstructionDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        using var configurationScope = _commandRunner.UseConfiguration(config);
        using var operation = await AcquireStructuredOperationAsync(context, config, cancellationToken);
        var identity = CaptureStructuredIdentity(config, context.Binding.BindingId, operation);
        return await _fileSystemExecutor.DiscoverScopedInstructionsAsync(
            config,
            request,
            identity.NamespaceFingerprint,
            cancellationToken);
    }

    private async Task<string?> EnsureContainerAsync(AgentExecutionTargetContext context, DockerExecutionRuntimeConfig config, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var container = ResolveContainerName(config, context.Binding.BindingId);
        _configService.EnsureHostMountPaths(config);
        var mounts = _configService.ResolveMounts(config);
        if (string.IsNullOrWhiteSpace(config.ImageReference))
        {
            throw new InvalidOperationException("Configure a Docker image before using Docker execution.");
        }

        var verifiedMounts = OpenVerifiedMounts(mounts, cancellationToken);
        try
        {
            var imageReference = config.ImageReference;
            var hostAccessPolicy = DockerHostAccessPolicy.Resolve();
            var daemonIdentity = await ResolveLocalDaemonIdentityAsync(cancellationToken);
            var imageIdentity = await ResolveImageIdentityAsync(imageReference, cancellationToken);
            if (config.ImageIdentity is { } expectedImageIdentity
                && !string.Equals(expectedImageIdentity, imageIdentity, StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "The Docker image identity changed after permission planning; explicit reapproval is required.");
            }
            var signature = BuildContainerSignature(
                config,
                verifiedMounts,
                imageIdentity,
                daemonIdentity,
                hostAccessPolicy);
            var inspect = await RunDockerAsync(["inspect", "-f", "{{.State.Running}} {{ index .Config.Labels \"sunder.resources.signature\" }}", container], cancellationToken);
            var existing = ParseInspectResult(inspect.Output);
            if (inspect.ExitCode == 0 && existing.Running)
            {
                if (string.Equals(existing.Signature, signature, StringComparison.Ordinal))
                {
                    await _mountIdentityVerifier.VerifyAsync(
                        container,
                        config,
                        verifiedMounts,
                        RunDockerAsync,
                        cancellationToken);
                    _verifiedContainerSignatures[container] = signature;
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
                        await _mountIdentityVerifier.VerifyAsync(
                            container,
                            config,
                            verifiedMounts,
                            RunDockerAsync,
                            cancellationToken);
                        _verifiedContainerSignatures[container] = signature;
                        return container;
                    }

                    throw new InvalidOperationException(FormatDockerContainerStartFailure(container, start.Output));
                }

                await RunDockerAsync(["rm", "-f", container], cancellationToken);
            }

            var root = DockerPathResolver.ResolveDefaultBaseDirectory(config);
            var args = new List<string> { "run", "--pull", "never", "-d", "--name", container, "--label", $"sunder.resources.signature={signature}", "-w", root };
            AddSecurityOptions(args);
            AddNonInteractiveEnvironment(args);
            AddOption(args, "--user", hostAccessPolicy.ContainerUser);
            foreach (var mount in mounts)
            {
                args.Add("--mount");
                args.Add(string.Concat(
                    "type=bind,source=", mount.HostPath,
                    ",target=", mount.ContainerPath));
            }

            args.Add(imageIdentity);
            args.Add("tail");
            args.Add("-f");
            args.Add("/dev/null");
            var run = await RunDockerAsync(args, cancellationToken);
            if (run.ExitCode == 0)
            {
                try
                {
                    await _mountIdentityVerifier.VerifyAsync(
                        container,
                        config,
                        verifiedMounts,
                        RunDockerAsync,
                        cancellationToken);
                    _verifiedContainerSignatures[container] = signature;
                    return container;
                }
                catch
                {
                    await RunDockerAsync(["rm", "-f", container], CancellationToken.None);
                    throw;
                }
            }

            throw new InvalidOperationException(FormatDockerContainerRunFailure(container, imageReference, run.Output));
        }
        finally
        {
            for (var index = verifiedMounts.Length - 1; index >= 0; index--)
            {
                verifiedMounts[index].Root.Dispose();
            }
        }
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

    private static void AddSecurityOptions(ICollection<string> args)
    {
        AddOption(args, "--security-opt", "no-new-privileges=true");
        AddOption(args, "--cap-drop", "ALL");
        AddOption(args, "--network", DefaultNetworkPolicy);
        AddOption(args, "--memory", DefaultMemoryLimit);
        AddOption(args, "--cpus", DefaultCpuLimit);
        AddOption(args, "--pids-limit", DefaultPidLimit);
        args.Add("--init");
    }

    private static void AddOption(ICollection<string> args, string name, string value)
    {
        args.Add(name);
        args.Add(value);
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
        IReadOnlyList<DockerVerifiedMount> mounts,
        string imageIdentity,
        string daemonIdentity,
        DockerHostAccessPolicy hostAccessPolicy)
    {
        var builder = new StringBuilder();
        builder.Append("security-policy:").AppendLine(ContainerSecurityPolicyVersion)
            .AppendLine(config.ImageReference ?? string.Empty)
            .AppendLine(imageIdentity)
            .AppendLine(daemonIdentity)
            .AppendLine(hostAccessPolicy.Signature)
            .AppendLine(config.DefaultWorkingDirectory ?? string.Empty)
            .AppendLine(config.ShellPath ?? string.Empty);

        foreach (var verified in mounts.OrderBy(item => item.Mount.ContainerPath, StringComparer.Ordinal))
        {
            var mount = verified.Mount;
            builder.Append("mount:")
                .Append(mount.HostPath).Append('|')
                .Append(mount.ContainerPath).Append('|')
                .Append(verified.Root.Handle.Identity).AppendLine();
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString()))).ToLowerInvariant();
    }

    private static DockerVerifiedMount[] OpenVerifiedMounts(
        IReadOnlyList<DockerExecutionMount> mounts,
        CancellationToken cancellationToken)
    {
        var opened = new List<DockerVerifiedMount>(mounts.Count);
        try
        {
            foreach (var mount in mounts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                opened.Add(new DockerVerifiedMount(
                    mount,
                    HostSecurePathEngine.OpenRoot(mount.HostPath, cancellationToken: cancellationToken)));
            }
            return opened.ToArray();
        }
        catch
        {
            for (var index = opened.Count - 1; index >= 0; index--)
            {
                opened[index].Root.Dispose();
            }
            throw;
        }
    }

}
