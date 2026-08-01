using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Storage;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentSessionCleanupDispatcher : IAsyncDisposable
{
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AgentLocalStore _store;
    private readonly AgentRpcCatalog _rpcCatalog;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private IReadOnlyDictionary<AgentSessionDataCleanerIdentity, AgentRpcReference<IAgentSessionDataCleaner>>
        _activeCleaners = new Dictionary<AgentSessionDataCleanerIdentity, AgentRpcReference<IAgentSessionDataCleaner>>();
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private bool _startupRecoveryPending;
    private bool _disposed;

    public AgentSessionCleanupDispatcher(
        AgentLocalStore store,
        AgentRpcCatalog rpcCatalog)
    {
        _store = store;
        _rpcCatalog = rpcCatalog;
        _store.SessionCleanupJobsChanged += Wake;
    }

    internal static void DispatchAvailableNow(
        AgentLocalStore store,
        AgentRpcCatalog rpcCatalog)
    {
        var dispatcher = new AgentSessionCleanupDispatcher(store, rpcCatalog);
        try
        {
            dispatcher.FlushAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        finally
        {
            dispatcher.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
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
            _rpcCatalog.Changed += OnCatalogChanged;
            ReconcileCleaners(DateTimeOffset.UtcNow);
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
            _rpcCatalog.Changed -= OnCatalogChanged;
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
        ReconcileCleaners(DateTimeOffset.UtcNow);
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
            dispatchPending = DispatchAvailable(cancellationToken);
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

    private bool DispatchAvailable(CancellationToken cancellationToken)
    {
        var dispatchCount = 0;
        var unavailable = new HashSet<AgentSessionDataCleanerIdentity>();
        while (dispatchCount < AgentLocalStore.MaxSessionCleanupJobsPerPass)
        {
            KeyValuePair<AgentSessionDataCleanerIdentity, AgentRpcReference<IAgentSessionDataCleaner>>[] cleaners;
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

                if (!reference.TryAcquire(out var lease))
                {
                    _store.ReleaseSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                    unavailable.Add(identity);
                    continue;
                }

                using (lease)
                {
                    try
                    {
                        lease.Service.DeleteSessionData(claim.SessionId);
                        if (lease.RetirementToken.IsCancellationRequested)
                        {
                            _store.ReleaseSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                            unavailable.Add(identity);
                        }
                        else
                        {
                            _store.CompleteSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                        }
                    }
                    catch (Exception exception)
                    {
                        if (lease.RetirementToken.IsCancellationRequested)
                        {
                            _store.ReleaseSessionCleanupJob(claim, DateTimeOffset.UtcNow);
                            unavailable.Add(identity);
                        }
                        else
                        {
                            _store.FailSessionCleanupJob(claim, exception, DateTimeOffset.UtcNow);
                        }
                    }
                }
            }

            if (!madeProgress)
            {
                return false;
            }
        }
        return true;
    }

    private void ReconcileCleaners(DateTimeOffset now)
    {
        var active = new Dictionary<
            AgentSessionDataCleanerIdentity,
            AgentRpcReference<IAgentSessionDataCleaner>>();
        foreach (var reference in _rpcCatalog.GetServiceReferences(AgentRpcServices.SessionCleaners))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                var packageId = lease.PackageId.Trim();
                var cleanerId = lease.Service.CleanerId?.Trim() ?? string.Empty;
                if (packageId.Length is < 1 or > 256 || cleanerId.Length is < 1 or > 512)
                {
                    continue;
                }
                active.TryAdd(new AgentSessionDataCleanerIdentity(packageId, cleanerId), reference);
            }
        }

        _store.RegisterSessionDataCleaners(active.Keys.ToArray(), now);
        lock (_syncRoot)
        {
            _activeCleaners = active;
        }
    }

    private void OnCatalogChanged(object? sender, AgentRpcCatalogChangedEventArgs e)
    {
        if (e.IncludesContract(AgentRpcContractIds.SessionCleaner)) Wake();
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
