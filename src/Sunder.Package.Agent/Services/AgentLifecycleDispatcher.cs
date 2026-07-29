using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services;

public sealed class AgentLifecycleDispatcher : IPackageBackgroundService, IAsyncDisposable
{
    internal const int MaxLifecycleDispatchesPerPass = 256;
    private static readonly TimeSpan LeaseDuration = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AgentLocalStore _store;
    private readonly IPackageExtensionInvocationCatalog? _invocationCatalog;
    private readonly IPackageExtensionCatalogMonitor? _extensionCatalogMonitor;
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

    public AgentLifecycleDispatcher(
        AgentLocalStore store,
        IPackageExtensionCatalog extensionCatalog,
        IPackageEventLogger? eventLogger = null)
    {
        _store = store;
        _invocationCatalog = extensionCatalog as IPackageExtensionInvocationCatalog;
        _extensionCatalogMonitor = extensionCatalog as IPackageExtensionCatalogMonitor;
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
                if (_extensionCatalogMonitor is not null)
                {
                    _extensionCatalogMonitor.Changed += OnExtensionCatalogChanged;
                }
                _startupRecoveryPending = true;
                ReconcileSubscriptions();
                _worker = RunAsync(lifetime.Token);
            }
            catch
            {
                if (_extensionCatalogMonitor is not null)
                {
                    _extensionCatalogMonitor.Changed -= OnExtensionCatalogChanged;
                }
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
            if (_extensionCatalogMonitor is not null)
            {
                _extensionCatalogMonitor.Changed -= OnExtensionCatalogChanged;
            }
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
        var recoveryPending = RecoverStartupDeliveries();
        var replayPending = ReconcileSubscriptions();
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
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["lifecycle.failure_code"] = "dispatcher_processing_failure",
                            ["exception.type"] = GetBoundedExceptionType(ex),
                        }).ConfigureAwait(false);
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
                    var outcome = await active.DeliverAsync(envelope, cancellationToken).ConfigureAwait(false);
                    if (outcome == LifecycleDeliveryOutcome.OwnerRetired)
                    {
                        _store.ReleaseLifecycleDelivery(claim, DateTimeOffset.UtcNow);
                        unavailableSubscriptions.Add(active.Subscription.SubscriptionId);
                    }
                    else
                    {
                        _store.CompleteLifecycleDelivery(claim, DateTimeOffset.UtcNow);
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _store.ReleaseLifecycleDelivery(claim, DateTimeOffset.UtcNow);
                    throw;
                }
                catch (Exception ex)
                {
                    var failure = _store.FailLifecycleDelivery(
                        claim,
                        ex,
                        DateTimeOffset.UtcNow,
                        ex is AgentDurableLifecycleIntegrityException);
                    await LogDeliveryFailureAsync(active.Subscription, claim.Event, failure).ConfigureAwait(false);
                }
            }

            if (!madeProgress)
            {
                return false;
            }
        }
        return true;
    }

    private bool ReconcileSubscriptions()
    {
        var now = DateTimeOffset.UtcNow;
        var replayPending = false;
        var active = new Dictionary<string, ActiveLifecycleSubscription>(StringComparer.Ordinal);
        var invocationCatalog = _invocationCatalog
            ?? throw new InvalidOperationException(
                "The host extension catalog does not support activation-scoped invocation leases.");
        foreach (var reference in invocationCatalog.GetExtensionReferences(PackageExtensionPoints.DurableLifecycleObservers))
        {
            if (reference.TryAcquire(out var lease))
            {
                using (lease)
                {
                    replayPending |= AddSubscription(
                        active,
                        lease.PackageId,
                        lease.Contribution.ObserverId,
                        lease.Contribution.DisplayName,
                        "Durable",
                        subscription => new DurableLifecycleSubscription(subscription, reference),
                        now);
                }
            }
        }

        foreach (var reference in invocationCatalog.GetExtensionReferences(PackageExtensionPoints.LifecycleObservers))
        {
            if (reference.TryAcquire(out var lease))
            {
                using (lease)
                {
                    replayPending |= AddSubscription(
                        active,
                        lease.PackageId,
                        lease.Contribution.ObserverId,
                        lease.Contribution.DisplayName,
                        "Compatibility",
                        subscription => new CompatibilityLifecycleSubscription(subscription, reference),
                        now);
                }
            }
        }

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
        string packageId,
        string observerId,
        string displayName,
        string contractKind,
        Func<AgentLifecycleSubscription, ActiveLifecycleSubscription> createSubscription,
        DateTimeOffset now)
    {
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
        active.Add(subscriptionId, createSubscription(subscription));
        return replayPending;
    }

    private static async ValueTask<LifecycleDeliveryOutcome> DeliverWithLeaseAsync<TObserver>(
        IPackageExtensionReference<TObserver> reference,
        AgentDurableLifecycleEventEnvelope envelope,
        CancellationToken cancellationToken,
        Func<TObserver, AgentDurableLifecycleEventEnvelope, CancellationToken, ValueTask> deliver)
    {
        if (!reference.TryAcquire(out var lease))
        {
            return LifecycleDeliveryOutcome.OwnerRetired;
        }

        using (lease)
        {
            var retirementToken = lease.RetirementToken;
            if (retirementToken.IsCancellationRequested)
            {
                return LifecycleDeliveryOutcome.OwnerRetired;
            }

            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                await deliver(lease.Contribution, envelope, invocation.Token).ConfigureAwait(false);
                return retirementToken.IsCancellationRequested
                    ? LifecycleDeliveryOutcome.OwnerRetired
                    : LifecycleDeliveryOutcome.Delivered;
            }
            catch (Exception) when (retirementToken.IsCancellationRequested)
            {
                return LifecycleDeliveryOutcome.OwnerRetired;
            }
        }
    }

    private static ValueTask DeliverCompatibilityAsync(
        IAgentLifecycleObserver observer,
        AgentDurableLifecycleEventEnvelope envelope,
        CancellationToken cancellationToken)
    {
        var payload = envelope.Payload;
        if (payload.ContentErased)
        {
            return ValueTask.CompletedTask;
        }
        if (payload.Session is null || payload.Run is null)
        {
            throw new AgentDurableLifecycleIntegrityException(
                $"Compatibility lifecycle event '{envelope.EventId}' has no run/session snapshot.");
        }

        var turnContext = new AgentTurnContextRecord(
            payload.Session,
            payload.Run,
            payload.UserMessage ?? string.Empty,
            payload.WorkingSummary);
        var compatibilityEvent = new AgentLifecycleEvent(
            envelope.Kind,
            payload.Session,
            payload.Run,
            turnContext,
            payload.Turns,
            payload.RecentLiveBufferTurns,
            payload.TriggerTurn,
            payload.Checkpoint)
        {
            EventId = envelope.EventId,
            Sequence = envelope.Sequence,
            PayloadHash = envelope.PayloadHash,
        };
        return observer.HandleLifecycleEventAsync(compatibilityEvent, cancellationToken);
    }

    private async Task LogDeliveryFailureAsync(
        AgentLifecycleSubscription subscription,
        AgentLifecycleOutboxRecord outboxEvent,
        AgentLifecycleDeliveryFailureResult failure)
    {
        var level = failure.Poisoned ? PackageLogLevel.Error : PackageLogLevel.Warning;
        var eventName = failure.Poisoned ? "lifecycle.delivery.poisoned" : "lifecycle.delivery.retry";
        var message = failure.Poisoned
            ? "A durable lifecycle delivery entered poison state and remains an ordering barrier until its recovery retry."
            : "A durable lifecycle delivery failed and was scheduled for bounded retry.";
        await LogSafelyAsync(
            level,
            eventName,
            message,
            new Dictionary<string, object?>(StringComparer.Ordinal)
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
            }).ConfigureAwait(false);
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

    private void OnExtensionCatalogChanged(object? sender, PackageExtensionCatalogChangedEventArgs e)
        => Wake();

    private static string GetBoundedExceptionType(Exception exception)
    {
        var exceptionType = exception.GetType().FullName ?? exception.GetType().Name;
        return exceptionType[..Math.Min(exceptionType.Length, 512)];
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

    private enum LifecycleDeliveryOutcome
    {
        Delivered,
        OwnerRetired,
    }

    private abstract record ActiveLifecycleSubscription(AgentLifecycleSubscription Subscription)
    {
        public abstract ValueTask<LifecycleDeliveryOutcome> DeliverAsync(
            AgentDurableLifecycleEventEnvelope envelope,
            CancellationToken cancellationToken);
    }

    private sealed record DurableLifecycleSubscription(
        AgentLifecycleSubscription Subscription,
        IPackageExtensionReference<IAgentDurableLifecycleObserver> Reference)
        : ActiveLifecycleSubscription(Subscription)
    {
        public override ValueTask<LifecycleDeliveryOutcome> DeliverAsync(
            AgentDurableLifecycleEventEnvelope envelope,
            CancellationToken cancellationToken)
            => DeliverWithLeaseAsync(
                Reference,
                envelope,
                cancellationToken,
                static (observer, item, token) => observer.HandleDurableLifecycleEventAsync(item, token));
    }

    private sealed record CompatibilityLifecycleSubscription(
        AgentLifecycleSubscription Subscription,
        IPackageExtensionReference<IAgentLifecycleObserver> Reference)
        : ActiveLifecycleSubscription(Subscription)
    {
        public override ValueTask<LifecycleDeliveryOutcome> DeliverAsync(
            AgentDurableLifecycleEventEnvelope envelope,
            CancellationToken cancellationToken)
            => DeliverWithLeaseAsync(
                Reference,
                envelope,
                cancellationToken,
                DeliverCompatibilityAsync);
    }
}
