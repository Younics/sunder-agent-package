using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

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
            if (_observation is not null) return;
            // This observer belongs to the package activation, not the request which
            // first subscribed to ProfileChanged. Inheriting its Worker invocation
            // would send that completed parent ID when catalogs reconnect later.
            if (ExecutionContext.IsFlowSuppressed()) Start();
            else { using (ExecutionContext.SuppressFlow()) Start(); }

            void Start() => _observation = Task.Run(() => Task.WhenAll(
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
        var recovering = new ObservationRecovery();
        while (!token.IsCancellationRequested)
        {
            try
            {
                var snapshot = await providers.ListAsync(token).ConfigureAwait(false);
                NotifyCapabilityObservationRecovery(ref recovering);
                await foreach (var change in providers.WatchAsync(snapshot.Revision, snapshot.Sequence, token).ConfigureAwait(false))
                {
                    recovering = default;
                    OnSelectableCapabilitiesChanged();
                    if (change.Kind == ServiceProviderChangeKind.ResetRequired) break;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                NotifyCapabilityObservationFailure(exception, ref recovering);
            }
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private async Task ObserveSelectableChangesAsync(CancellationToken token)
    {
        var recovering = new ObservationRecovery();
        while (!token.IsCancellationRequested)
        {
            using var subscription = CancellationTokenSource.CreateLinkedTokenSource(token);
            Task[] watches = [];
            try
            {
                var snapshot = await _selectableProviders.ListAsync(token).ConfigureAwait(false);
                NotifyCapabilityObservationRecovery(ref recovering);
                watches = snapshot.Providers.Where(provider => provider.State == ServiceProviderState.Active)
                    .Select(provider => ObserveCapabilityChangesAsync(provider, subscription.Token)).ToArray();
                await foreach (var change in _selectableProviders.WatchAsync(snapshot.Revision, snapshot.Sequence, token).ConfigureAwait(false))
                {
                    recovering = default;
                    OnSelectableCapabilitiesChanged();
                    break; // Membership changed: replace the business subscriptions using a fresh snapshot.
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                NotifyCapabilityObservationFailure(exception, ref recovering);
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
        var recovering = new ObservationRecovery();
        while (!token.IsCancellationRequested && !provider.RetirementToken.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in provider.Service.WatchAsync(token).WithCancellation(token).ConfigureAwait(false))
                {
                    recovering = default;
                    OnSelectableCapabilitiesChanged();
                }
                NotifyCapabilityObservationRecovery(ref recovering);
                return; // A provider may explicitly expose no domain invalidation stream.
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception exception) when (!AgentServiceExceptionPolicy.IsFatal(exception))
            {
                NotifyCapabilityObservationFailure(exception, ref recovering);
            }
            if (provider.RetirementToken.IsCancellationRequested) return;
            try { await Task.Delay(TimeSpan.FromMilliseconds(250), token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }

    private struct ObservationRecovery
    {
        internal bool Recovering;
        internal bool SnapshotHintPublished;
    }

    private void NotifyCapabilityObservationFailure(Exception error, ref ObservationRecovery state)
    {
        // A rejected request is not a catalog change. Repeated recovery attempts
        // must not cause more UI catalog fan-out every 250 ms while capacity is low.
        if (!state.Recovering && error is not SunderRpcException { Error.Kind: SunderRpcErrorKind.ResourceExhausted })
            OnSelectableCapabilitiesChanged();
        state.Recovering = true;
    }

    private void NotifyCapabilityObservationRecovery(ref ObservationRecovery state)
    {
        if (!state.Recovering || state.SnapshotHintPublished) return;
        state.SnapshotHintPublished = true;
        // A fresh snapshot/stream may include changes missed during the outage.
        // A successful List followed by a rejected Watch is not a recovered stream;
        // only a real event resets the outage and permits another recovery hint.
        OnSelectableCapabilitiesChanged();
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
