using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Shared.Presentation;

internal readonly record struct PresentationOperation(
    string Name,
    string FailureMessage,
    string? NotificationKey = null,
    bool PropagateCancellation = false)
{
    public string EffectiveNotificationKey => NotificationKey ?? Name;
}

internal sealed class PresentationOperationObserver : IDisposable, IAsyncDisposable
{
    private static readonly TimeSpan NotificationDeduplicationWindow = TimeSpan.FromSeconds(2);
    private readonly object _notificationSyncRoot = new();
    private readonly Dictionary<string, long> _notificationTimestamps = new(StringComparer.Ordinal);
    private readonly string _component;
    private readonly Func<PackageLogLevel, string, string, PresentationOperation, Exception, ValueTask>?
        _writeEvent;
    private readonly Func<PackageNotificationRequest, ValueTask>? _publishNotification;
    private readonly PresentationTaskScope _submissions = new(
        PresentationTaskScope.PropagateExpectedFailure);
    private int _disposed;

    public PresentationOperationObserver(
        IPackageContext packageContext,
        IPackageNotificationService notifications,
        string component)
    {
        ArgumentNullException.ThrowIfNull(packageContext);
        ArgumentNullException.ThrowIfNull(notifications);
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        _component = component;
        _writeEvent = (level, eventName, message, operation, exception) =>
            packageContext.Logging.Events.WriteAsync(
                level,
                eventName,
                message,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["presentation.component"] = component,
                    ["presentation.operation"] = operation.Name,
                },
                exception);
        _publishNotification = request => notifications.PublishAsync(request);
    }

    private PresentationOperationObserver(string component)
    {
        _component = component;
    }

    internal PresentationOperationObserver(
        string component,
        Func<PackageLogLevel, string, string, PresentationOperation, Exception, ValueTask>?
            writeEvent,
        Func<PackageNotificationRequest, ValueTask>? publishNotification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(component);
        _component = component;
        _writeEvent = writeEvent;
        _publishNotification = publishNotification;
    }

    public CancellationToken CancellationToken => _submissions.CancellationToken;

    internal static PresentationOperationObserver CreateUnreported(string component)
        => new(component);

    public Task RunAsync(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<CancellationToken, Task>? reconcile = null,
        CancellationToken cancellationToken = default)
    {
        Validate(operation);
        ArgumentNullException.ThrowIfNull(action);
        return _submissions.RunAsync(_ => RunCoreAsync(
            operation,
            action,
            reconcile is null ? null : (_, token) => reconcile(token),
            cancellationToken));
    }

    public Task RunAsync(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<Exception, CancellationToken, Task> reconcile,
        CancellationToken cancellationToken = default)
    {
        Validate(operation);
        ArgumentNullException.ThrowIfNull(action);
        ArgumentNullException.ThrowIfNull(reconcile);
        return _submissions.RunAsync(_ => RunCoreAsync(
            operation,
            action,
            reconcile,
            cancellationToken));
    }

    private async Task RunCoreAsync(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<Exception, CancellationToken, Task>? reconcile,
        CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _submissions.CancellationToken);
        try
        {
            await action(linkedCancellation.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!PresentationExceptionPolicy.IsFatal(exception))
        {
            if (operation.PropagateCancellation
                && !_submissions.CancellationToken.IsCancellationRequested)
            {
                throw;
            }

            if (_submissions.CancellationToken.IsCancellationRequested
                || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            await HandleExpectedFailureAsync(operation, exception, reconcile).ConfigureAwait(false);
        }
        catch (Exception exception) when (PresentationExceptionPolicy.IsExpectedOperationFailure(exception))
        {
            if (_submissions.CancellationToken.IsCancellationRequested)
            {
                return;
            }

            await HandleExpectedFailureAsync(operation, exception, reconcile).ConfigureAwait(false);
        }
        catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
        {
            await TryWriteAsync(
                PackageLogLevel.Error,
                "presentation.operation.unexpected_failure",
                $"{_component} operation '{operation.Name}' failed unexpectedly.",
                operation,
                exception).ConfigureAwait(false);
            throw;
        }
    }

    public void Submit(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<CancellationToken, Task>? reconcile = null)
    {
        Validate(operation);
        ArgumentNullException.ThrowIfNull(action);
        _submissions.Run(_ => RunCoreAsync(
            operation,
            action,
            reconcile is null ? null : (_, token) => reconcile(token),
            cancellationToken: default));
    }

    public Task SubmitAsync(
        PresentationOperation operation,
        Func<CancellationToken, Task> action,
        Func<CancellationToken, Task>? reconcile = null)
    {
        return RunAsync(
            operation,
            action,
            reconcile,
            cancellationToken: default);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _submissions.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Exchange(ref _disposed, 1);
        await _submissions.DisposeAsync().ConfigureAwait(false);
    }

    private async Task HandleExpectedFailureAsync(
        PresentationOperation operation,
        Exception exception,
        Func<Exception, CancellationToken, Task>? reconcile)
    {
        var reconciliationFailed = false;
        await TryWriteAsync(
            PackageLogLevel.Warning,
            "presentation.operation.expected_failure",
            $"{_component} operation '{operation.Name}' failed with an expected user-operation error.",
            operation,
            exception).ConfigureAwait(false);

        if (reconcile is not null && !IsDisposed)
        {
            try
            {
                await reconcile(exception, _submissions.CancellationToken).ConfigureAwait(false);
            }
            catch (Exception reconciliationException) when (!PresentationExceptionPolicy.IsFatal(reconciliationException))
            {
                await TryWriteAsync(
                    PackageLogLevel.Error,
                    "presentation.operation.reconciliation_failure",
                    $"{_component} operation '{operation.Name}' could not reconcile presentation state.",
                    operation,
                    reconciliationException).ConfigureAwait(false);
                if (!PresentationExceptionPolicy.IsExpectedOperationFailure(reconciliationException))
                {
                    throw;
                }

                reconciliationFailed = true;
            }
        }

        if (!TryBeginNotification(operation.EffectiveNotificationKey) || IsDisposed)
        {
            return;
        }

        try
        {
            if (_publishNotification is null)
            {
                return;
            }

            await _publishNotification(new PackageNotificationRequest(
                "Operation not completed",
                reconciliationFailed
                    ? "The operation could not be completed, and current state could not be refreshed. Retry or reopen the view."
                    : operation.FailureMessage,
                PackageNotificationDisplayMode.ToastOnly,
                PackageNotificationSeverity.Warning)).ConfigureAwait(false);
        }
        catch (Exception notificationException) when (!PresentationExceptionPolicy.IsFatal(notificationException))
        {
            await TryWriteAsync(
                PackageLogLevel.Error,
                "presentation.operation.notification_failure",
                $"{_component} operation '{operation.Name}' notification could not be published.",
                operation,
                notificationException).ConfigureAwait(false);
        }
    }

    private async Task TryWriteAsync(
        PackageLogLevel level,
        string eventName,
        string message,
        PresentationOperation operation,
        Exception exception)
    {
        if (_writeEvent is null)
        {
            return;
        }

        try
        {
            await _writeEvent(
                level,
                eventName,
                message,
                operation,
                exception).ConfigureAwait(false);
        }
        catch (Exception loggingException) when (!PresentationExceptionPolicy.IsFatal(loggingException))
        {
            // The failure is observed here; diagnostics cannot recursively fail the UI boundary.
        }
    }

    private bool TryBeginNotification(string key)
    {
        var now = Environment.TickCount64;
        lock (_notificationSyncRoot)
        {
            if (_notificationTimestamps.TryGetValue(key, out var previous)
                && TimeSpan.FromMilliseconds(now - previous) < NotificationDeduplicationWindow)
            {
                return false;
            }

            _notificationTimestamps[key] = now;
            return true;
        }
    }

    private bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    private static void Validate(PresentationOperation operation)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.Name);
        ArgumentException.ThrowIfNullOrWhiteSpace(operation.FailureMessage);
    }
}
