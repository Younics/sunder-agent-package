using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionTarget
    : IAgentProcessExecutionTarget, IAgentWorkspaceBindingContributor, IAgentExecutionScopeProvider, IAgentExecutionResourceResolver, IAgentExecutionPathMapper, IAgentExecutionPathEnvironment
{
    private readonly LocalExecutionWorkspaceConfigService _configService;
    private readonly LocalShellExecutor _shellExecutor;
    private readonly LocalProcessExecutor _processExecutor;

    public LocalExecutionTarget(IPackageContext packageContext, LocalExecutionWorkspaceConfigService configService, LocalShellCatalogService shellCatalogService)
    {
        _configService = configService;
        _shellExecutor = new LocalShellExecutor(packageContext, shellCatalogService);
        _processExecutor = new LocalProcessExecutor(packageContext);
    }

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
        return ValueTask.FromResult(_shellExecutor.GetShell(_configService.GetConfig(context.Binding.BindingId)));
    }

    public ValueTask<AgentExecutionScopeDescriptor> GetExecutionScopeAsync(
        AgentExecutionTargetContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = _configService.GetConfig(context.Binding.BindingId);
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
        return ValueTask.FromResult(LocalResourceResolver.ResolveResources(resources));
    }

    public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
        AgentExecutionTargetContext context,
        string path,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = _configService.GetConfig(context.Binding.BindingId);
        return ValueTask.FromResult(LocalResourceResolver.ResolveFileResource(config, path, allowOutsideConfiguredScope: true));
    }

    public async ValueTask<AgentShellCommandResult> ExecuteShellAsync(
        AgentExecutionTargetContext context,
        AgentShellCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.GetConfig(context.Binding.BindingId);
        return await _shellExecutor.ExecuteShellAsync(config, context, request, cancellationToken);
    }

    public async ValueTask<AgentShellCommandResult> ExecuteProcessAsync(
        AgentExecutionTargetContext context,
        AgentProcessCommandRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.GetConfig(context.Binding.BindingId);
        return await _processExecutor.ExecuteProcessAsync(config, context, request, cancellationToken);
    }

    public ValueTask<AgentExecutionPathMapping> MapToHostPathAsync(
        AgentExecutionTargetContext context,
        string executionPath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = _configService.GetConfig(context.Binding.BindingId);
        return ValueTask.FromResult(LocalResourceResolver.MapToHostPath(config, executionPath));
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
        var pathEntry = Path.GetFullPath(LocalExecutionWorkspaceConfigService.ExpandPath(executionPath.Trim()));
        var pathEntries = (config.PathEntries ?? [])
            .Append(pathEntry)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        _configService.SaveConfig(context.Binding.BindingId, config with { PathEntries = pathEntries });
        return ValueTask.CompletedTask;
    }

    public async ValueTask<AgentFileReadResult> ReadFileAsync(
        AgentExecutionTargetContext context,
        AgentFileReadRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.GetConfig(context.Binding.BindingId);
        return await LocalFileSystemExecutor.ReadFileAsync(config, request, context.AllowOutsideConfiguredScope, cancellationToken);
    }

    public async ValueTask<AgentFileMutationResult> WriteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileWriteRequest request,
        CancellationToken cancellationToken = default)
    {
        var config = _configService.GetConfig(context.Binding.BindingId);
        return await LocalFileSystemExecutor.WriteFileAsync(config, request, context.AllowOutsideConfiguredScope, cancellationToken);
    }

    public ValueTask<AgentFileMutationResult> DeleteFileAsync(
        AgentExecutionTargetContext context,
        AgentFileDeleteRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var config = _configService.GetConfig(context.Binding.BindingId);
        return LocalFileSystemExecutor.DeleteFileAsync(config, request, context.AllowOutsideConfiguredScope);
    }

    internal string ResolvePath(LocalExecutionWorkspaceConfig config, string path, bool allowOutsideConfiguredScope)
        => LocalPathResolver.ResolvePath(config, path, allowOutsideConfiguredScope);

    private AgentExecutionTargetReadiness GetReadinessCore(AgentWorkspaceBindingRecord binding)
    {
        var config = _configService.GetConfig(binding.BindingId);
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
}
