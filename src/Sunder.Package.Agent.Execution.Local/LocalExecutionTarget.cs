using Sunder.Agent.Execution.Common;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionTarget
    : IAgentProcessExecutionTarget, IAgentStructuredFileSearchExecutionTarget, IAgentRangedFileExecutionTarget, IAgentExecutionScopeProvider, IAgentExecutionResourceResolver, IAgentExecutionPathMapper, IAgentExecutionPathEnvironment, IAgentScopedInstructionDiscoveryTarget, IAgentResourceAuthorityExecutionTarget, IDisposable
{
    private readonly LocalExecutionWorkspaceConfigService _configService;
    private readonly IPackageContext _packageContext;
    private readonly LocalShellCatalogService _shellCatalogService;
    private readonly LocalShellExecutor _shellExecutor;
    private readonly LocalProcessExecutor _processExecutor;
    private readonly LocalResourceReference _resourceReferences;

    public LocalExecutionTarget(IPackageContext packageContext, LocalExecutionWorkspaceConfigService configService, LocalShellCatalogService shellCatalogService)
        : this(packageContext, configService, shellCatalogService, new LocalResourceReference())
    {
    }

    internal LocalExecutionTarget(
        IPackageContext packageContext,
        LocalExecutionWorkspaceConfigService configService,
        LocalShellCatalogService shellCatalogService,
        LocalResourceReference resourceReferences)
    {
        _packageContext = packageContext;
        _configService = configService;
        _shellCatalogService = shellCatalogService;
        _shellExecutor = new LocalShellExecutor(packageContext, shellCatalogService);
        _processExecutor = new LocalProcessExecutor(packageContext);
        _resourceReferences = resourceReferences;
    }

    public AgentExecutionTargetDescriptor Descriptor { get; } = new(
        "local",
        "local",
        "Local Machine",
        "Executes commands and file operations on this machine within configured workspace paths.",
        SupportsShell: true,
        SupportsFiles: true)
    {
        Facets =
        [
            AgentExecutionFacetIds.ProcessExecution,
            AgentExecutionFacetIds.ResourceResolution,
            AgentExecutionFacetIds.ResourceAuthority,
            AgentExecutionFacetIds.ExecutionScope,
            AgentExecutionFacetIds.PathMapping,
            AgentExecutionFacetIds.PathEnvironment,
            AgentExecutionFacetIds.RangedFileRead,
            AgentExecutionFacetIds.StructuredFileSearch,
            AgentExecutionFacetIds.ScopedInstructionDiscovery,
        ],
    };

    public async ValueTask<string?> GetConfigurationGenerationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => LocalExecutionWorkspaceConfigService.CreateGeneration(
            await CaptureCurrentConfigurationAsync(context.Binding.BindingId, cancellationToken));

    public async ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await GetReadinessCoreAsync(context, cancellationToken);
    }

    public async ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LocalShellExecutor.GetShellDescriptor(
            (await GetCurrentConfigurationAsync(context, cancellationToken)).SelectedShell);
    }

    public async ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return new AgentExecutionScopeDescriptor(
            Descriptor.DisplayName,
            config.WorkspacePaths,
            config.DefaultWorkingDirectory,
            "Local filesystem paths for this machine. On Windows, use the exact configured drive and user profile paths shown here.");
    }

    public ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        AgentExecutionTargetContext context,
        IReadOnlyList<AgentExecutionResourceDescriptor> resources,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(LocalResourceResolver.ResolveResources(resources));
    }

    public async ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return LocalResourceResolver.ResolveFileResource(
            config,
            path,
            allowOutsideConfiguredScope: true,
            context,
            _resourceReferences);
    }

    public async ValueTask<AgentResourceAuthorityValidation> ValidateResourceAuthorityAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return LocalSecurePathEngine.HasCurrentOutsideCapabilities(config, context, _resourceReferences)
            ? new AgentResourceAuthorityValidation(true)
            : new AgentResourceAuthorityValidation(
                false,
                LocalResourceReference.ReapprovalRequiredErrorCode,
                "Outside Local resource authority expired or is unavailable; explicit reapproval is required.");
    }

    public void ReleaseResourceAuthority(IReadOnlyList<string> resourceCapabilities)
    {
        foreach (var capability in resourceCapabilities)
        {
            _resourceReferences.Revoke(capability);
        }
    }

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await _shellExecutor.ExecuteShellAsync(config, context, request, cancellationToken);
    }

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await _processExecutor.ExecuteProcessAsync(config, context, request, cancellationToken);
    }

    public async ValueTask<AgentFileSearchResult> ExecuteFileSearchAsync(
        AgentExecutionTargetContext context,
        AgentFileSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await LocalSecureFileSearch.ExecuteAsync(
            config,
            request,
            context.AllowOutsideConfiguredScope,
            context.ApprovedResourceReferences,
            cancellationToken,
            authorizationContext: context,
            resourceReferences: _resourceReferences);
    }

    public async ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return LocalResourceResolver.MapToHostPath(config, executionPath);
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

        var pathEntry = Path.GetFullPath(LocalExecutionWorkspaceConfigService.ExpandPath(executionPath.Trim()));
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
                        LocalExecutionWorkspaceConfigService.CreateGeneration(snapshot),
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        "Local execution configuration changed after permission planning; explicit reapproval is required.");
                }

                return config with
                {
                    PathEntries = (config.PathEntries ?? [])
                        .Append(pathEntry)
                        .Distinct(LocalExecutionWorkspaceConfigService.PathStringComparer)
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
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await LocalFileSystemExecutor.ReadFileAsync(
            config,
            request,
            context.AllowOutsideConfiguredScope,
            cancellationToken,
            context.ApprovedResourceReferences,
            authorizationContext: context,
            resourceReferences: _resourceReferences);
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await LocalFileSystemExecutor.WriteFileAsync(
            config,
            request,
            context.AllowOutsideConfiguredScope,
            cancellationToken,
            approvedResourceReferences: context.ApprovedResourceReferences,
            authorizationContext: context,
            resourceReferences: _resourceReferences);
    }

    public async ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await LocalFileSystemExecutor.DeleteFileAsync(
            config,
            request,
            context.AllowOutsideConfiguredScope,
            cancellationToken,
            context.ApprovedResourceReferences,
            authorizationContext: context,
            resourceReferences: _resourceReferences);
    }

    public async ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverScopedInstructionsAsync(
        AgentExecutionTargetContext context,
        AgentScopedInstructionDiscoveryRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await LocalScopedInstructionDiscovery.DiscoverAsync(
            await BuildRuntimeConfigAsync(context, cancellationToken),
            request,
            cancellationToken);
    }

    internal string ResolvePath(LocalExecutionRuntimeConfig config, string path, bool allowOutsideConfiguredScope)
        => LocalPathResolver.ResolvePath(config, path, allowOutsideConfiguredScope);

    internal LocalResourceReference ResourceReferences => _resourceReferences;

    public void Dispose() => _resourceReferences.Dispose();

    private async Task<AgentExecutionTargetReadiness> GetReadinessCoreAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken)
    {
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        if (config.WorkspacePaths.Count == 0)
        {
            return new AgentExecutionTargetReadiness(Descriptor.TargetKind, Descriptor.TargetId, AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure at least one workspace path before using local execution.");
        }

        foreach (var rootPath in config.WorkspacePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var root = LocalSecurePathEngine.OpenRoot(rootPath, cancellationToken: cancellationToken);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
            {
                return new AgentExecutionTargetReadiness(
                    Descriptor.TargetKind,
                    Descriptor.TargetId,
                    AgentExecutionTargetReadinessStatus.Failed,
                    $"Workspace path cannot be opened with strict no-follow security: {rootPath}. {ex.Message}");
            }
        }

        return new AgentExecutionTargetReadiness(
            Descriptor.TargetKind,
            Descriptor.TargetId,
            AgentExecutionTargetReadinessStatus.Ready,
            LocalSecureNative.StrictMutationsAvailable
                ? "Local execution is ready."
                : "Local reads, search, and scoped-instruction discovery are ready; strict structured writes and deletes are unavailable on Windows.");
    }

    private async Task<LocalExecutionRuntimeConfig> BuildRuntimeConfigAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await GetCurrentConfigurationAsync(context, cancellationToken);
        var config = snapshot.WorkspaceConfig;
        var paths = context.Workspace.Paths
            .Where(path => !string.IsNullOrWhiteSpace(path.HostPath))
            .OrderBy(path => path.SortOrder)
            .Select(path => Path.GetFullPath(LocalExecutionWorkspaceConfigService.ExpandPath(path.HostPath.Trim())))
            .Distinct(LocalExecutionWorkspaceConfigService.PathStringComparer)
            .ToArray();
        var defaultPath = context.Workspace.Paths
            .OrderBy(path => path.SortOrder)
            .FirstOrDefault(path => path.IsDefault && !string.IsNullOrWhiteSpace(path.HostPath))
            ?.HostPath;
        var defaultWorkingDirectory = string.IsNullOrWhiteSpace(defaultPath)
            ? paths.FirstOrDefault()
            : Path.GetFullPath(LocalExecutionWorkspaceConfigService.ExpandPath(defaultPath.Trim()));
        if (defaultWorkingDirectory is not null
            && !paths.Any(path => LocalExecutionWorkspaceConfigService.IsSameOrChildPath(defaultWorkingDirectory, path)))
        {
            defaultWorkingDirectory = paths.FirstOrDefault();
        }

        return new LocalExecutionRuntimeConfig(paths, defaultWorkingDirectory, config.SelectedShellId, config.PathEntries)
        {
            SelectedShell = snapshot.SelectedShell,
            DefaultTimeoutSeconds = snapshot.DefaultTimeoutSeconds,
        };
    }

    private async Task<LocalExecutionConfigurationSnapshot> GetCurrentConfigurationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken)
    {
        var snapshot = await CaptureCurrentConfigurationAsync(context.Binding.BindingId, cancellationToken);
        if (context.ExpectedConfigurationGeneration is { } expected
            && !string.Equals(
                expected,
                LocalExecutionWorkspaceConfigService.CreateGeneration(snapshot),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Local execution configuration changed after permission planning; explicit reapproval is required.");
        }

        return snapshot;
    }

    private async Task<LocalExecutionConfigurationSnapshot> CaptureCurrentConfigurationAsync(
        string bindingId,
        CancellationToken cancellationToken,
        LocalExecutionWorkspaceConfig? workspaceConfig = null)
    {
        var config = workspaceConfig
                     ?? await _configService.GetConfigAsync(bindingId, cancellationToken);
        return new LocalExecutionConfigurationSnapshot(
            config,
            await _shellCatalogService.ResolveShellAsync(config.SelectedShellId, cancellationToken),
            await LocalExecutionConfiguration.ResolveDefaultTimeoutSecondsAsync(
                _packageContext,
                cancellationToken));
    }
}
