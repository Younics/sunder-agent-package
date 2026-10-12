using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Tests;

// Owns a generated receiver scope, not individual provider entries. The source,
// execution target and transient authority still cross the actual RPC boundary.
internal sealed class GeneratedToolSourceProbe : IDisposable, IAsyncDisposable
{
    private readonly GeneratedServiceTestScope _receiver;
    private readonly SunderRpcProviderSnapshot _toolSnapshot;
    private readonly SunderRpcProviderSnapshot? _promptSnapshot;
    private readonly SunderRpcProviderSnapshot? _cleanerSnapshot;
    private readonly SunderRpcProviderSnapshot? _selectableCapabilitySnapshot;
    private ServiceProvider<AgentServices.IToolSourceService>? _tool;
    private ServiceProvider<AgentServices.IPromptContextContributorService>? _prompt;
    private ServiceProvider<AgentServices.ISessionCleanerService>? _cleaner;
    private ServiceProvider<AgentServices.ISelectableCapabilityProviderService>? _selectableCapabilities;

    internal GeneratedToolSourceProbe(RegressionTestExtensionCatalog catalog, object source, string packageId,
        bool withPromptContext = false, bool withSessionCleaner = false, bool withSelectableCapabilities = false)
    {
        _toolSnapshot = catalog.AddProvider(RegressionTestBindings.ToolSources, source, packageId);
        if (withPromptContext) _promptSnapshot = catalog.AddProvider(RegressionTestBindings.PromptContextContributors, source, packageId);
        if (withSessionCleaner) _cleanerSnapshot = catalog.AddProvider(RegressionTestBindings.SessionCleaners, source, packageId);
        if (withSelectableCapabilities) _selectableCapabilitySnapshot = catalog.AddProvider(
            RegressionTestBindings.SelectableCapabilityProviders, source, packageId);
        _receiver = new GeneratedServiceTestScope(catalog.RpcForCaller("sunder.package.agent"));
    }

    internal async ValueTask<AgentToolResult> ExecuteAsync(AgentToolExecutionContext context, AgentToolRequest request,
        CancellationToken token = default)
        => await (await ToolAsync(token)).Service.ExecuteAsync(context, request, token);

    internal async ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken token = default)
        => await (await ToolAsync(token)).Service.ListToolsAsync(context, token);

    internal async ValueTask<AgentToolReadiness?> GetReadinessAsync(string toolId, AgentToolSourceContext context, CancellationToken token = default)
        => await (await ToolAsync(token)).Service.GetReadinessAsync(toolId, context, token);

    internal async ValueTask<AgentPermissionRequest?> BuildPermissionRequestAsync(AgentToolExecutionContext context, AgentToolRequest request,
        CancellationToken token = default)
        => await (await ToolAsync(token)).Service.PermissionAsync(context, request, token);

    internal async ValueTask<AgentToolPresentation?> PresentationAsync(AgentToolPresentationRequest request, CancellationToken token = default)
        => await (await ToolAsync(token)).Service.PresentationAsync(request, token);

    internal async ValueTask<AgentToolResult?> PreflightExecutionAsync(AgentToolExecutionContext context, AgentToolRequest request,
        CancellationToken token = default)
        => await (await ToolAsync(token)).Service.PreflightAsync(context, request, token);

    internal async ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
        AgentProfileSelectableCapabilityRequest request, CancellationToken token = default)
        => await (await SelectableCapabilitiesAsync(token)).Service.ListAsync(request.Profile, token);

    internal async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken token = default)
        => await (await PromptAsync(token)).Service.ContributeAsync(request, token);

    internal async ValueTask AcknowledgePromptContextAsync(AgentPromptContextReceipt receipt, CancellationToken token = default)
        => await (await PromptAsync(token)).Service.AcknowledgeAsync(receipt, token);

    internal async ValueTask DeleteSessionAsync(Guid sessionId, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        _cleaner ??= await SelectAsync<AgentServices.ISessionCleanerService>(_cleanerSnapshot
            ?? throw new InvalidOperationException("This probe has no session cleaner."), token);
        await _cleaner.Service.DeleteSessionAsync(sessionId, token);
    }

    private async ValueTask<ServiceProvider<AgentServices.IToolSourceService>> ToolAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return _tool ??= await SelectAsync<AgentServices.IToolSourceService>(_toolSnapshot, token);
    }

    private async ValueTask<ServiceProvider<AgentServices.IPromptContextContributorService>> PromptAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return _prompt ??= await SelectAsync<AgentServices.IPromptContextContributorService>(_promptSnapshot
            ?? throw new InvalidOperationException("This probe has no prompt-context provider."), token);
    }

    private async ValueTask<ServiceProvider<AgentServices.ISelectableCapabilityProviderService>> SelectableCapabilitiesAsync(
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return _selectableCapabilities ??= await SelectAsync<AgentServices.ISelectableCapabilityProviderService>(
            _selectableCapabilitySnapshot
            ?? throw new InvalidOperationException("This probe has no selectable-capability provider."), token);
    }

    private async ValueTask<ServiceProvider<T>> SelectAsync<T>(SunderRpcProviderSnapshot added, CancellationToken token) where T : class
    {
        var catalog = await _receiver.Providers<T>().ListAsync(token);
        return catalog.Providers.SingleOrDefault(provider => provider.ActivationId == added.ActivationId
            && provider.ProviderId == added.ProviderId)
            ?? throw new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "test.provider-withdrawn",
                "The probe's selected provider was withdrawn before discovery."));
    }

    public void Dispose() => Task.Run(async () => await DisposeAsync()).GetAwaiter().GetResult();
    public ValueTask DisposeAsync() => _receiver.DisposeAsync();
}
