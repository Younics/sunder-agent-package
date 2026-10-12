using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentProfileService
{
    private readonly object _observationSync = new();
    private readonly CancellationTokenSource _observationLifetime = new();
    private Task? _observation;

    private void StartCapabilityObservation()
    {
        lock (_observationSync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _observation ??= Task.Run(() => Task.WhenAll(
                ObserveCatalogChangesAsync(_chatProviders, _observationLifetime.Token),
                ObserveCatalogChangesAsync(_embeddingProviders, _observationLifetime.Token),
                ObserveCatalogChangesAsync(_behaviorLoops, _observationLifetime.Token),
                ObserveCatalogChangesAsync(_capabilityConsumers, _observationLifetime.Token),
                ObserveCatalogChangesAsync(_toolSources, _observationLifetime.Token),
                ObserveCatalogChangesAsync(_executionTargets, _observationLifetime.Token),
                ObserveSelectableChangesAsync(_observationLifetime.Token)));
        }
    }

    // These streams only invalidate Agent's combined presentation. The SDK owns
    // exact bindings, retirement and catalog recovery; no provider state is rebuilt here.
    private async Task ObserveCatalogChangesAsync<T>(IServiceProviders<T> providers, CancellationToken token) where T : class
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snapshot = await providers.ListAsync(token).ConfigureAwait(false);
                await foreach (var change in providers.WatchAsync(snapshot.Revision, snapshot.Sequence, token).ConfigureAwait(false))
                {
                    OnSelectableCapabilitiesChanged();
                    if (change.Kind == ServiceProviderChangeKind.ResetRequired) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                OnSelectableCapabilitiesChanged();
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private async Task ObserveSelectableChangesAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            using var subscription = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task[] watches = [];
            try
            {
                var snapshot = await _selectableProviders.ListAsync(token).ConfigureAwait(false);
                watches = snapshot.Providers.Where(provider => provider.State == ServiceProviderState.Active)
                    .Select(provider => ObserveCapabilityChangesAsync(provider, subscription.Token)).ToArray();
                await foreach (var change in _selectableProviders.WatchAsync(snapshot.Revision, snapshot.Sequence, token).ConfigureAwait(false))
                {
                    OnSelectableCapabilitiesChanged();
                    break; // Membership changed: replace the business subscriptions using a fresh snapshot.
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                OnSelectableCapabilitiesChanged();
            }
            finally
            {
                await subscription.CancelAsync().ConfigureAwait(false);
                await Task.WhenAll(watches).ConfigureAwait(false);
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private async Task ObserveCapabilityChangesAsync(
        ServiceProvider<AgentServices.ISelectableCapabilityProviderService> provider, CancellationToken token)
    {
        while (!token.IsCancellationRequested && !provider.RetirementToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in provider.Service.WatchAsync(token).WithCancellation(token).ConfigureAwait(false))
                    OnSelectableCapabilitiesChanged();
                return; // A provider may explicitly expose no domain invalidation stream.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                OnSelectableCapabilitiesChanged();
            }
            if (provider.RetirementToken.IsCancellationRequested) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    public void Dispose()
    {
        lock (_observationSync)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _observationLifetime.Cancel();
    }

    public async ValueTask DisposeAsync()
    {
        Dispose();
        if (_observation is not null) await _observation.ConfigureAwait(false);
        _observationLifetime.Dispose();
    }
}
