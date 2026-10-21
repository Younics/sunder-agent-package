using Sunder.Agent.Execution.Common;
using IExecutionTargetService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Execution_Target.IExecutionTargetService;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Execution.Local;

[SunderServiceProvider(typeof(IExecutionTargetService), ProviderId = "local.execution.target")]
public sealed partial class LocalExecutionTarget : IExecutionTargetService, IDisposable, Sunder.Sdk.Worker.ISunderWorkerSettingsSchemaProvider
{
    private readonly LocalExecutionWorkspaceConfigService _configService;
    private readonly IPackageContext _packageContext;
    private readonly LocalShellCatalogService _shellCatalogService;
    private readonly LocalShellExecutor _shellExecutor;
    private readonly LocalProcessExecutor _processExecutor;
    private readonly LocalResourceReference _resourceReferences;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
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

    public Sunder.Sdk.Settings.PackageSettingsSchema SettingsSchema => LocalExecutionConfiguration.Schema;

    public AgentExecutionTargetDescriptor Descriptor { get; } = new()
    {
        TargetKind = "local", TargetId = "local", DisplayName = "Local Machine",
        Description = "Executes commands and file operations on this machine within configured workspace paths.",
        SupportsShell = true, SupportsFiles = true,
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

    public ValueTask<AgentExecutionTargetDescriptor> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(Descriptor); }

    public async ValueTask<string?> ConfigurationGenerationAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
        => LocalExecutionWorkspaceConfigService.CreateGeneration(
            await CaptureCurrentConfigurationAsync(context.Binding.BindingId, cancellationToken));

    public async ValueTask<AgentExecutionTargetReadiness> ReadinessAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return await GetReadinessCoreAsync(context, cancellationToken);
        }
        catch (LocalExecutionDomainException exception)
        {
            return Readiness(AgentExecutionTargetReadinessStatus.Failed, $"{exception.Message} (code: {exception.Code})");
        }
    }

    public async ValueTask<AgentExecutionShellDescriptor> ShellAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return LocalShellExecutor.GetShellDescriptor(
            (await GetCurrentConfigurationAsync(context, cancellationToken)).SelectedShell);
    }

    public async ValueTask<AgentExecutionScopeDescriptor> ExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return new AgentExecutionScopeDescriptor
        {
            DisplayName = Descriptor.DisplayName, WorkspacePaths = config.WorkspacePaths, DefaultWorkingDirectory = config.DefaultWorkingDirectory,
            PathStyleDescription = "Local filesystem paths for this machine. On Windows, use the exact configured drive and user profile paths shown here.",
        };
    }

    public ValueTask<IReadOnlyList<AgentResolvedExecutionResource>> ResolveResourcesAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetResolveResourcesRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(LocalResourceResolver.ResolveResources(request.Resources));
    }

    public async ValueTask<AgentResolvedResource> ResolveFileAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetResolveFileRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (context, path) = (request.Context, request.Path);
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
            ? new() { IsValid = true, ErrorCode = null, ErrorMessage = null }
            : new() { IsValid = false, ErrorCode = LocalResourceReference.ReapprovalRequiredErrorCode,
                ErrorMessage = "Outside Local resource authority expired or is unavailable; explicit reapproval is required." };
    }

    public ValueTask ReleaseResourceAuthorityAsync(IReadOnlyList<string> resourceCapabilities, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        foreach (var capability in resourceCapabilities)
        {
            _resourceReferences.Revoke(capability);
        }
        return ValueTask.CompletedTask;
    }

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetExecuteShellRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await _shellExecutor.ExecuteShellAsync(config, context, request, cancellationToken);
    }

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetExecuteProcessRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
        var config = await BuildRuntimeConfigAsync(context, cancellationToken);
        return await _processExecutor.ExecuteProcessAsync(config, context, request, cancellationToken);
    }

    public async ValueTask<AgentFileSearchResult> StructuredFileSearchAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetStructuredFileSearchRequest invocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (context, request) = (invocation.Context, invocation.Request);
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

    public async ValueTask<AgentExecutionPathMapping> MapPathAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetMapPathRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = await BuildRuntimeConfigAsync(request.Context, cancellationToken);
        return LocalResourceResolver.MapToHostPath(config, request.Path);
    }

    public async ValueTask<IReadOnlyList<string>> ListPathAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return (await GetCurrentConfigurationAsync(context, cancellationToken)).WorkspaceConfig.PathEntries ?? [];
    }

    public async ValueTask AddPathAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetAddPathRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (context, executionPath) = (request.Context, request.Path);
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
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetReadFileRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
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
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetWriteFileRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
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
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetDeleteFileRequest invocation,
        CancellationToken cancellationToken = default)
    {
        var (context, request) = (invocation.Context, invocation.Request);
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

    public async ValueTask<AgentScopedInstructionDiscoveryResult> DiscoverInstructionsAsync(
        CYounics_Ai_Agent_Execution_Target_ExecutionTargetDiscoverInstructionsRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await LocalScopedInstructionDiscovery.DiscoverAsync(
            await BuildRuntimeConfigAsync(request.Context, cancellationToken),
            request.Request,
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
            return Readiness(AgentExecutionTargetReadinessStatus.NeedsConfiguration, "Configure at least one workspace path before using local execution.");
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
                return Readiness(AgentExecutionTargetReadinessStatus.Failed, $"Workspace path cannot be opened with strict no-follow security: {rootPath}. {ex.Message}");
            }
        }

        return Readiness(AgentExecutionTargetReadinessStatus.Ready,
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

    private AgentExecutionTargetReadiness Readiness(AgentExecutionTargetReadinessStatus status, string message) => new()
    { TargetKind = Descriptor.TargetKind, TargetId = Descriptor.TargetId, Status = status, Message = message };

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
