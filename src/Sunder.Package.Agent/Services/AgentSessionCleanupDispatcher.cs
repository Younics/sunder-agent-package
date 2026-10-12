using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentSessionCleanupDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AgentLocalStore _store;
    private readonly IServiceProviders<AgentServices.ISessionCleanerService> _cleaners;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private IReadOnlyDictionary<AgentSessionDataCleanerIdentity, ServiceProvider<AgentServices.ISessionCleanerService>>
        _activeCleaners = new Dictionary<AgentSessionDataCleanerIdentity, ServiceProvider<AgentServices.ISessionCleanerService>>();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private bool _startupRecoveryPending;
    private bool _disposed;

    public AgentSessionCleanupDispatcher(
        AgentLocalStore store,
        IServiceProviders<AgentServices.ISessionCleanerService> cleaners)
    {
        _store = store;
        _cleaners = cleaners;
        _store.SessionCleanupJobsChanged += Wake;
    }

    internal static async Task DispatchAvailableNowAsync(
        AgentLocalStore store,
        IServiceProviders<AgentServices.ISessionCleanerService> cleaners)
    {
        await using var dispatcher = new AgentSessionCleanupDispatcher(store, cleaners);
        await dispatcher.FlushAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                return Task.CompletedTask;
            }

            _lifetime = new CancellationTokenSource();
            _startupRecoveryPending = true;
            _worker = RunAsync(_lifetime.Token);
        }
        Wake();
        return Task.CompletedTask;
    }

    internal async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? worker;
        CancellationTokenSource? lifetime;
        lock (_syncRoot)
        {
            worker = _worker;
            lifetime = _lifetime;
            if (lifetime is null)
            {
                return;
            }
            _worker = null;
            _lifetime = null;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        Wake();
        try
        {
            if (worker is not null)
            {
                await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested
                                                  && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    internal Task SignalStopAsync()
        => AgentRuntimeWorkerCancellation.SignalAsync(Volatile.Read(ref _lifetime));

    internal async Task FlushAsync(CancellationToken cancellationToken = default)
    {
        _store.EnsureRuntimeGenerationCurrent();
        await ReconcileCleanersAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        var recoveryPending = false;
        lock (_syncRoot)
        {
            if (_startupRecoveryPending)
            {
                var recovered = _store.RecoverSessionCleanupJobLeases(DateTimeOffset.UtcNow);
                _startupRecoveryPending = recovered == AgentLocalStore.MaxSessionCleanupJobsPerPass;
                recoveryPending = _startupRecoveryPending;
            }
        }

        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var dispatchPending = false;
        try
        {
            dispatchPending = await DispatchAvailableAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dispatchGate.Release();
        }
        if (recoveryPending || dispatchPending)
        {
            Wake();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_syncRoot)
        {
            _disposed = true;
        }
        _store.SessionCleanupJobsChanged -= Wake;
        _dispatchGate.Dispose();
        _wakeSignal.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wakeSignal.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                await FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // Individual jobs remain durable; polling retries isolated dispatcher failures.
            }
        }
    }

    private async Task<bool> DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        var dispatchCount = 0;
        var unavailable = new HashSet<AgentSessionDataCleanerIdentity>();
        while (dispatchCount < AgentLocalStore.MaxSessionCleanupJobsPerPass)
        {
            KeyValuePair<AgentSessionDataCleanerIdentity, ServiceProvider<AgentServices.ISessionCleanerService>>[] cleaners;
            lock (_syncRoot)
            {
                cleaners = _activeCleaners
                    .Where(item => !unavailable.Contains(item.Key))
                    .OrderBy(item => item.Key.PackageId, StringComparer.Ordinal)
                    .ThenBy(item => item.Key.CleanerId, StringComparer.Ordinal)
                    .ToArray();
            }

            var madeProgress = false;
            foreach (var (identity, reference) in cleaners)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (dispatchCount >= AgentLocalStore.MaxSessionCleanupJobsPerPass)
                {
                    return true;
                }

                var claim = _store.TryClaimSessionCleanupJob(
                    identity,
                    DateTimeOffset.UtcNow,
                    LeaseDuration);
                if (claim is null)
                {
                    continue;
                }
                madeProgress = true;
                dispatchCount++;

                try
                {
                    await reference.Service.DeleteSessionAsync(claim.SessionId, cancellationToken).ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (reference.RetirementToken.IsCancellationRequested)
                    {
                        _store.ReleaseSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                        unavailable.Add(identity);
                    }
                    else _store.CompleteSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _store.ReleaseSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                    throw;
                }
                catch (Exception exception) when (reference.RetirementToken.IsCancellationRequested
                    || exception is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable })
                {
                    _store.ReleaseSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                    unavailable.Add(identity);
                }
                catch (Exception exception)
                {
                    _store.FailSessionCleanupJob(claim, exception, DateTimeOffset.UtcNow);
                }
            }

            if (!madeProgress)
            {
                return false;
            }
        }
        return true;
    }

    private async Task ReconcileCleanersAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var active = new Dictionary<
            AgentSessionDataCleanerIdentity,
            ServiceProvider<AgentServices.ISessionCleanerService>>();
        var catalog = await _cleaners.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                var packageId = provider.PackageId.Trim();
                var cleanerId = descriptor.Id?.Trim() ?? string.Empty;
                if (packageId.Length is < 1 or > 256 || cleanerId.Length is < 1 or > 512)
                {
                    continue;
                }
                active.TryAdd(new AgentSessionDataCleanerIdentity(packageId, cleanerId), provider);
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable) { }
        }

        _store.RegisterSessionDataCleaners(active.Keys.ToArray(), now);
        lock (_syncRoot)
        {
            _activeCleaners = active;
        }
    }

    private void Wake()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
            {
                _wakeSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }
}
