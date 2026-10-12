using Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Services;

internal sealed class SubagentEditorCapabilityCatalog(
    IServiceProviders<IToolSourceService> tools,
    IServiceProviders<ISelectableCapabilityProviderService> capabilities,
    IServiceProviders<IChatProviderService> chats,
    IServiceProviders<IRuntimeCatalogService> runtimes) : IDisposable, IAsyncDisposable
{
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task _completion = Task.CompletedTask;
    private Task? _stop;
    private bool _started;
    private bool _disposed;
    public event Action? Changed;

    internal ValueTask StartAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                _completion = Task.WhenAll(Task.Run(() => WatchMetadataAsync(tools)), Task.Run(() => WatchMetadataAsync(chats)),
                    Task.Run(() => WatchMetadataAsync(runtimes)), Task.Run(WatchCapabilitiesAsync));
            }
        }
        return ValueTask.CompletedTask;
    }

    public async Task<IReadOnlyList<AgentToolDescriptor>> ListLocalToolsAsync(CancellationToken cancellationToken = default)
    {
        var context = new AgentToolSourceContext { SessionId = null, Profile = null, Workspace = null, ExecutionBinding = null,
            ExecutionTarget = null, ExecutionTargetConfigurationGeneration = null };
        var descriptors = new List<AgentToolDescriptor>();
        var sources = await SubagentCatalogSelection.SnapshotAsync(tools, cancellationToken,
            static (source, token) => source.DescribeAsync(token)).ConfigureAwait(false);
        foreach (var source in sources.OrderBy(item => item.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var contributed = await SubagentCatalogSelection.InvokeOptionalAsync(source, cancellationToken,
                (service, token) => service.ListToolsAsync(context, token)).ConfigureAwait(false);
            if (contributed.IsAvailable) descriptors.AddRange(contributed.Value);
        }
        return descriptors.Where(value => value.SelectionScope == AgentToolSelectionScope.Tool)
            .GroupBy(value => string.Concat(value.SourceId ?? string.Empty, "\n", value.ToolId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListPackageCapabilitiesAsync(CancellationToken cancellationToken = default)
    {
        var values = new List<AgentProfileSelectableCapabilityDescriptor>();
        var providers = await SubagentCatalogSelection.SnapshotAsync(capabilities, cancellationToken,
            static (service, token) => service.DescribeAsync(token)).ConfigureAwait(false);
        foreach (var provider in providers.OrderBy(item => item.Metadata.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            var contributed = await SubagentCatalogSelection.InvokeOptionalAsync(provider, cancellationToken,
                static (service, token) => service.ListAsync(null, token)).ConfigureAwait(false);
            if (contributed.IsAvailable) values.AddRange(contributed.Value);
        }
        return values.Where(value => !string.IsNullOrWhiteSpace(value.Kind) && !string.IsNullOrWhiteSpace(value.CapabilityId) && !string.IsNullOrWhiteSpace(value.DisplayName))
            .GroupBy(value => string.Concat(value.Kind, "\n", value.SourceId ?? string.Empty, "\n", value.CapabilityId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First()).OrderBy(value => value.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private async Task WatchMetadataAsync<T>(IServiceProviders<T> providers) where T : class
    {
        long revision = 0, sequence = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in providers.WatchAsync(revision, sequence, _lifetime.Token).ConfigureAwait(false))
                {
                    revision = change.Revision; sequence = change.Sequence;
                    if (change.Kind == ServiceProviderChangeKind.ResetRequired)
                    {
                        var snapshot = await providers.ListAsync(_lifetime.Token).ConfigureAwait(false);
                        revision = snapshot.Revision; sequence = snapshot.Sequence;
                    }
                    NotifyChanged();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SunderRpcException or OperationCanceledException) { NotifyChanged(); }
            if (!await RetryAsync(_lifetime.Token).ConfigureAwait(false)) return;
        }
    }

    private async Task WatchCapabilitiesAsync()
    {
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                var snapshot = await capabilities.ListAsync(_lifetime.Token).ConfigureAwait(false);
                using var subscriptions = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                var watches = snapshot.Providers.Where(value => value.State == ServiceProviderState.Active)
                    .Select(provider => WatchContributionAsync(provider, subscriptions.Token)).ToArray();
                try
                {
                    NotifyChanged();
                    await foreach (var _ in capabilities.WatchAsync(snapshot.Revision, snapshot.Sequence, _lifetime.Token).ConfigureAwait(false)) break;
                }
                finally { await subscriptions.CancelAsync().ConfigureAwait(false); await Task.WhenAll(watches).ConfigureAwait(false); }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SunderRpcException or OperationCanceledException) { NotifyChanged(); }
            if (!await RetryAsync(_lifetime.Token).ConfigureAwait(false)) return;
        }
    }

    private async Task WatchContributionAsync(ServiceProvider<ISelectableCapabilityProviderService> provider, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, provider.RetirementToken);
        while (!lifetime.IsCancellationRequested)
        {
            try
            {
                await provider.EnsureCurrentAsync(lifetime.Token).ConfigureAwait(false);
                await foreach (var _ in provider.Service.WatchAsync(lifetime.Token).ConfigureAwait(false)) NotifyChanged();
                NotifyChanged();
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { return; }
            catch (SunderRpcException) { NotifyChanged(); }
            if (!await RetryAsync(lifetime.Token).ConfigureAwait(false)) return;
        }
    }

    private void NotifyChanged()
    {
        if (Changed is not { } handlers) return;
        foreach (Action handler in handlers.GetInvocationList())
        { try { handler(); } catch { /* Independent editor observers own their failures. */ } }
    }
    private static async Task<bool> RetryAsync(CancellationToken token)
    { try { await Task.Delay(250, token).ConfigureAwait(false); return true; } catch (OperationCanceledException) when (token.IsCancellationRequested) { return false; } }
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; } _lifetime.Cancel(); }
    public ValueTask DisposeAsync() { Dispose(); lock (_gate) return new(_stop ??= StopAsync()); }
    private async Task StopAsync() { try { await _completion.ConfigureAwait(false); } finally { _lifetime.Dispose(); } }
}
