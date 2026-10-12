using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Services;

public sealed class AgentLifecycleDispatcher : IAsyncDisposable
{
    internal const int MaxLifecycleDispatchesPerPass = 256;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AgentLocalStore _store;
    private readonly IServiceProviders<AgentServices.IDurableLifecycleObserverService> _observers;
    private readonly IPackageEventLogger _eventLogger;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _dispatchGate = new(1, 1);
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);

    private IReadOnlyDictionary<string, ActiveLifecycleSubscription> _activeSubscriptions =
        new Dictionary<string, ActiveLifecycleSubscription>(StringComparer.Ordinal);
    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private bool _startupRecoveryPending;
    private bool _disposed;

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal AgentLifecycleDispatcher(AgentLocalStore store,
        IServiceProviders<AgentServices.IDurableLifecycleObserverService> observers, IPackageContext context)
        : this(store, observers, context.Logging.Events) { }

    public AgentLifecycleDispatcher(
        AgentLocalStore store,
        IServiceProviders<AgentServices.IDurableLifecycleObserverService> observers,
        IPackageEventLogger? eventLogger = null)
    {
        _store = store;
        _observers = observers;
        _eventLogger = eventLogger ?? NullPackageLogging.Instance.Events;
        _store.LifecycleOutboxChanged += Wake;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                return Task.CompletedTask;
            }

            var lifetime = new CancellationTokenSource();
            _lifetime = lifetime;
            try
            {
                _startupRecoveryPending = true;
                _worker = RunAsync(lifetime.Token);
            }
            catch
            {
                _lifetime = null;
                lifetime.Dispose();
                throw;
            }
        }
        Wake();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
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
        cancellationToken.ThrowIfCancellationRequested();
        _store.EnsureRuntimeGenerationCurrent();
        var recoveryPending = RecoverStartupDeliveries();
        var replayPending = await ReconcileSubscriptionsAsync(cancellationToken).ConfigureAwait(false);
        var maintenancePending = _store.MaintainLifecycleState(DateTimeOffset.UtcNow);
        var dispatchPending = false;
        await _dispatchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            dispatchPending = await DispatchAvailableAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _dispatchGate.Release();
        }
        if (recoveryPending || replayPending || maintenancePending || dispatchPending)
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
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _store.LifecycleOutboxChanged -= Wake;
        _dispatchGate.Dispose();
        _wakeSignal.Dispose();
        GC.SuppressFinalize(this);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
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
                catch (Exception ex)
                {
                    await LogSafelyAsync(
                        PackageLogLevel.Error,
                        "lifecycle.dispatch.failed",
                        "The durable lifecycle dispatcher encountered an isolated processing failure.",
                        CreateProcessingFailureAttributes(ex)).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // An observer canceled during shutdown is released by DispatchAvailableAsync for lease recovery.
        }
    }

    private async Task<bool> DispatchAvailableAsync(CancellationToken cancellationToken)
    {
        var dispatchCount = 0;
        var unavailableSubscriptions = new HashSet<string>(StringComparer.Ordinal);
        while (dispatchCount < MaxLifecycleDispatchesPerPass)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ActiveLifecycleSubscription[] subscriptions;
            lock (_syncRoot)
            {
                subscriptions = _activeSubscriptions.Values
                    .Where(item => !unavailableSubscriptions.Contains(item.Subscription.SubscriptionId))
                    .OrderBy(item => item.Subscription.SubscriptionId, StringComparer.Ordinal)
                    .ToArray();
            }

            var madeProgress = false;
            foreach (var active in subscriptions)
            {
                if (dispatchCount >= MaxLifecycleDispatchesPerPass)
                {
                    return true;
                }
                cancellationToken.ThrowIfCancellationRequested();
                var claim = _store.TryClaimLifecycleDelivery(
                    active.Subscription.SubscriptionId,
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
                    var envelope = claim.Event.ToEnvelope();
                    await active.Provider.Service.HandleAsync(envelope, cancellationToken).ConfigureAwait(false);
                    _store.CompleteLifecycleDelivery(claim, DateTimeOffset.UtcNow);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _store.ReleaseLifecycleDelivery(claim, DateTimeOffset.UtcNow);
                    throw;
                }
                catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                    && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
                {
                    _store.ReleaseLifecycleDelivery(claim, DateTimeOffset.UtcNow);
                    unavailableSubscriptions.Add(active.Subscription.SubscriptionId);
                }
                catch (Exception ex)
                {
                    var failure = _store.FailLifecycleDelivery(
                        claim,
                        ex,
                        DateTimeOffset.UtcNow,
                        ex is AgentDurableLifecycleIntegrityException);
                    await LogDeliveryFailureAsync(active.Subscription, claim.Event, failure, ex).ConfigureAwait(false);
                }
            }

            if (!madeProgress)
            {
                return false;
            }
        }
        return true;
    }

    private async Task<bool> ReconcileSubscriptionsAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var replayPending = false;
        var active = new Dictionary<string, ActiveLifecycleSubscription>(StringComparer.Ordinal);
        ServiceProviderCatalog<AgentServices.IDurableLifecycleObserverService>? catalog;
        try
        {
            catalog = await _observers.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable)
        {
            catalog = null;
        }
        foreach (var observer in catalog?.Providers ?? [])
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (observer.State != ServiceProviderState.Active) continue;
            try
            {
                var identity = await observer.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                replayPending |= AddSubscription(active, observer, identity.Id, identity.DisplayName, now);
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                // The existing bounded outbox poll reconciles reappearing owners.
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            _activeSubscriptions = active;
        }
        return replayPending;
    }

    private bool RecoverStartupDeliveries()
    {
        lock (_syncRoot)
        {
            if (!_startupRecoveryPending)
            {
                return false;
            }

            var now = DateTimeOffset.UtcNow;
            var leases = _store.RecoverLifecycleDeliveryLeases(now);
            var poisoned = _store.RecoverPoisonedLifecycleDeliveries(now);
            _startupRecoveryPending = leases == AgentLocalStore.MaxLifecycleReconciliationBatchSize
                                      || poisoned == AgentLocalStore.MaxLifecycleReconciliationBatchSize;
            return _startupRecoveryPending;
        }
    }

    private bool AddSubscription(
        IDictionary<string, ActiveLifecycleSubscription> active,
        ServiceProvider<AgentServices.IDurableLifecycleObserverService> provider,
        string observerId,
        string displayName,
        DateTimeOffset now)
    {
        var packageId = provider.PackageId;
        const string contractKind = "Durable";
        if (string.IsNullOrWhiteSpace(observerId) || observerId.Trim().Length > 512)
        {
            _ = LogSafelyAsync(
                PackageLogLevel.Error,
                "lifecycle.subscription.invalid",
                "A lifecycle observer was ignored because its stable observer id was empty or too long.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["package.id"] = packageId,
                    ["lifecycle.contract"] = contractKind,
                });
            return false;
        }

        var normalizedObserverId = observerId.Trim();
        var subscriptionId = AgentLocalStore.BuildLifecycleSubscriptionId(
            packageId,
            normalizedObserverId,
            contractKind);
        var subscription = new AgentLifecycleSubscription(
            subscriptionId,
            packageId,
            normalizedObserverId,
            contractKind,
            string.IsNullOrWhiteSpace(displayName) ? normalizedObserverId : displayName.Trim());
        if (active.ContainsKey(subscriptionId))
        {
            _ = LogSafelyAsync(
                PackageLogLevel.Error,
                "lifecycle.subscription.duplicate",
                "A duplicate lifecycle observer subscription was ignored.",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["package.id"] = packageId,
                    ["observer.id"] = normalizedObserverId,
                    ["lifecycle.contract"] = contractKind,
                });
            return false;
        }

        var replayPending = _store.ReconcileLifecycleSubscription(subscription, now);
        active.Add(subscriptionId, new ActiveLifecycleSubscription(subscription, provider));
        return replayPending;
    }

    private async Task LogDeliveryFailureAsync(
        AgentLifecycleSubscription subscription,
        AgentLifecycleOutboxRecord outboxEvent,
        AgentLifecycleDeliveryFailureResult failure,
        Exception exception)
    {
        var level = failure.Poisoned ? PackageLogLevel.Error : PackageLogLevel.Warning;
        var eventName = failure.Poisoned ? "lifecycle.delivery.poisoned" : "lifecycle.delivery.retry";
        var message = failure.Poisoned
            ? "A durable lifecycle delivery entered poison state and remains an ordering barrier until its recovery retry."
            : "A durable lifecycle delivery failed and was scheduled for bounded retry.";
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["package.id"] = subscription.PackageId,
            ["observer.id"] = subscription.ObserverId,
            ["lifecycle.contract"] = subscription.ContractKind,
            ["lifecycle.event_id"] = outboxEvent.EventId,
            ["lifecycle.event_type"] = outboxEvent.Kind.ToString(),
            ["lifecycle.sequence"] = outboxEvent.Sequence,
            ["lifecycle.attempt"] = failure.AttemptCount,
            ["lifecycle.poisoned"] = failure.Poisoned,
            ["lifecycle.next_attempt_at_utc"] = failure.NextAttemptAtUtc,
            ["lifecycle.failure_code"] = failure.FailureCode,
            ["exception.type"] = failure.ExceptionType,
        };
        AddRpcFailureDiagnostics(attributes, exception);
        await LogSafelyAsync(
            level,
            eventName,
            message,
            attributes).ConfigureAwait(false);
    }

    private async Task LogSafelyAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        IReadOnlyDictionary<string, object?>? attributes)
    {
        try
        {
            await _eventLogger.WriteAsync(level, eventName, message, attributes).ConfigureAwait(false);
        }
        catch
        {
            // Diagnostics must not affect source or delivery state.
        }
    }

    private static string GetBoundedExceptionType(Exception exception)
    {
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        return exceptionType[..Math.Min(exceptionType.Length, 512)];
    }

    internal static IReadOnlyDictionary<string, object?> CreateProcessingFailureAttributes(Exception exception)
    {
        var attributes = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["lifecycle.failure_code"] = "dispatcher_processing_failure",
            ["exception.type"] = GetBoundedExceptionType(exception),
        };
        AddRpcFailureDiagnostics(attributes, exception);
        return attributes;
    }

    private static void AddRpcFailureDiagnostics(
        IDictionary<string, object?> attributes,
        Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.TryPop(out var current))
        {
            if (current is SunderRpcException rpcException)
            {
                attributes["rpc.error_kind"] = rpcException.Error.Kind.ToString();
                attributes["rpc.error_code"] = Bound(rpcException.Error.Code, 256);
                return;
            }
            if (current is AggregateException aggregate)
            {
                for (var index = aggregate.InnerExceptions.Count - 1; index >= 0; index--)
                {
                    pending.Push(aggregate.InnerExceptions[index]);
                }
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }
    }

    private static string Bound(string? value, int maximumLength)
        => string.IsNullOrWhiteSpace(value)
            ? "rpc.unknown"
            : value[..Math.Min(value.Length, maximumLength)];

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

    private sealed record ActiveLifecycleSubscription(
        AgentLifecycleSubscription Subscription,
        ServiceProvider<AgentServices.IDurableLifecycleObserverService> Provider);

}
