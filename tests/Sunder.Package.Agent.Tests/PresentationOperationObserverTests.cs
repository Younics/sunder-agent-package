extern alias AgentCore;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Xunit;
using PresentationOperation = AgentCore::Sunder.Package.Agent.Shared.Presentation.PresentationOperation;
using PresentationOperationObserver = AgentCore::Sunder.Package.Agent.Shared.Presentation.PresentationOperationObserver;

namespace Sunder.Package.Agent.Tests;

public sealed class PresentationOperationObserverTests
{
    private static readonly PresentationOperation Operation = new(
        "test.operation",
        "The test operation could not be completed.");

    [Fact]
    public async Task RunAsync_ExpectedRpcFailureIsLoggedNotifiedAndReconciledOnce()
    {
        var logs = new ConcurrentQueue<(string EventName, Exception Exception)>();
        var notifications = new ConcurrentQueue<PackageNotificationRequest>();
        var reconciliationCount = 0;
        using var observer = new PresentationOperationObserver(
            "test",
            (_, eventName, _, _, exception) =>
            {
                logs.Enqueue((eventName, exception));
                return ValueTask.CompletedTask;
            },
            request =>
            {
                notifications.Enqueue(request);
                return ValueTask.CompletedTask;
            });
        var failure = ExpectedFailure("provider detail that must not be shown");

        await observer.RunAsync(
            Operation,
            _ => Task.FromException(failure),
            _ =>
            {
                Interlocked.Increment(ref reconciliationCount);
                return Task.CompletedTask;
            });

        var log = Assert.Single(logs);
        Assert.Equal("presentation.operation.expected_failure", log.EventName);
        Assert.Same(failure, log.Exception);
        var notification = Assert.Single(notifications);
        Assert.Equal(
            Operation.FailureMessage,
            notification.GetType().GetProperty("Message")!.GetValue(notification));
        Assert.DoesNotContain("provider detail", notification.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, reconciliationCount);
    }

    [Fact]
    public async Task RunAsync_IndependentlyCanceledExceptionIsReconciledAndReported()
    {
        using var privateCancellation = new CancellationTokenSource();
        privateCancellation.Cancel();
        var notifications = new ConcurrentQueue<PackageNotificationRequest>();
        var reconciliationCount = 0;
        using var observer = new PresentationOperationObserver(
            "test",
            writeEvent: null,
            request =>
            {
                notifications.Enqueue(request);
                return ValueTask.CompletedTask;
            });

        await observer.RunAsync(
            Operation,
            _ => Task.FromException(new OperationCanceledException(privateCancellation.Token)),
            _ =>
            {
                Interlocked.Increment(ref reconciliationCount);
                return Task.CompletedTask;
            });

        Assert.Equal(1, reconciliationCount);
        Assert.Single(notifications);
    }

    [Fact]
    public async Task RunAsync_ReconciliationFailureDoesNotClaimStateWasRefreshed()
    {
        PackageNotificationRequest? notification = null;
        using var observer = new PresentationOperationObserver(
            "test",
            writeEvent: null,
            request =>
            {
                notification = request;
                return ValueTask.CompletedTask;
            });

        await observer.RunAsync(
            Operation,
            _ => Task.FromException(ExpectedFailure("operation failed")),
            _ => Task.FromException(new IOException("reconciliation failed")));

        var published = Assert.IsType<PackageNotificationRequest>(notification);
        var message = Assert.IsType<string>(published.GetType().GetProperty("Message")!.GetValue(published));
        Assert.Contains("could not be refreshed", message, StringComparison.Ordinal);
        Assert.DoesNotContain("reconciliation failed", message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RunAsync_UnknownFailureRemainsObservable()
    {
        using var observer = PresentationOperationObserver.CreateUnreported("test");
        var failure = new InvalidOperationException("invariant");
        var reconciliationCount = 0;

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => observer.RunAsync(
            Operation,
            _ => Task.FromException(failure),
            _ =>
            {
                Interlocked.Increment(ref reconciliationCount);
                return Task.CompletedTask;
            }));

        Assert.Same(failure, thrown);
        Assert.Equal(0, reconciliationCount);
    }

    public static TheoryData<Exception> FatalFailures => new()
    {
        new OutOfMemoryException("fatal"),
        new StackOverflowException("fatal"),
        new AccessViolationException("fatal"),
        new SEHException("fatal"),
        new Exception("wrapped", new OutOfMemoryException("fatal")),
        new AggregateException(ExpectedFailure("expected"), new SEHException("fatal")),
    };

    [Theory]
    [MemberData(nameof(FatalFailures))]
    public async Task RunAsync_FatalAndWrappedFatalFailuresPropagate(Exception failure)
    {
        using var observer = PresentationOperationObserver.CreateUnreported("test");

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => observer.RunAsync(
            Operation,
            _ => Task.FromException(failure)));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task RunAsync_ReportingAndReconciliationFailuresAreObservedWithoutRecursion()
    {
        var loggingAttempts = 0;
        var notificationAttempts = 0;
        var reconciliationAttempts = 0;
        using var observer = new PresentationOperationObserver(
            "test",
            (_, _, _, _, _) =>
            {
                Interlocked.Increment(ref loggingAttempts);
                return ValueTask.FromException(new InvalidOperationException("logging failed"));
            },
            _ =>
            {
                Interlocked.Increment(ref notificationAttempts);
                return ValueTask.FromException(new InvalidOperationException("notification failed"));
            });

        await observer.RunAsync(
            Operation,
            _ => Task.FromException(ExpectedFailure("operation failed")),
            _ =>
            {
                Interlocked.Increment(ref reconciliationAttempts);
                return Task.FromException(new InvalidOperationException("reconciliation failed"));
            });

        Assert.True(loggingAttempts >= 3);
        Assert.Equal(1, notificationAttempts);
        Assert.Equal(1, reconciliationAttempts);
    }

    [Fact]
    public async Task RunAsync_FatalNotificationFailurePropagates()
    {
        var fatal = new OutOfMemoryException("fatal notification");
        using var observer = new PresentationOperationObserver(
            "test",
            writeEvent: null,
            _ => ValueTask.FromException(fatal));

        var thrown = await Assert.ThrowsAsync<OutOfMemoryException>(() => observer.RunAsync(
            Operation,
            _ => Task.FromException(ExpectedFailure("operation failed"))));

        Assert.Same(fatal, thrown);
    }

    [Fact]
    public async Task ICommandExecute_ExpectedFailureDoesNotReachSynchronizationContext()
    {
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var notificationPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            using var observer = new PresentationOperationObserver(
                "test",
                writeEvent: null,
                _ =>
                {
                    notificationPublished.TrySetResult();
                    return ValueTask.CompletedTask;
                });
            var command = new AsyncRelayCommand(() => observer.RunAsync(
                Operation,
                async _ =>
                {
                    await Task.Yield();
                    throw ExpectedFailure("expected");
                }));

            command.Execute(null);
            await notificationPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
            await Task.Delay(25);

            Assert.Empty(context.Exceptions);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task Submit_UnknownFailureIsForwardedToSynchronizationContext()
    {
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var failure = new InvalidOperationException("invariant");
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            using var observer = PresentationOperationObserver.CreateUnreported("test");

            observer.Submit(Operation, async _ =>
            {
                await Task.Yield();
                throw failure;
            });

            var observed = await context.Failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Same(failure, observed);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static SunderRpcException ExpectedFailure(string message)
        => new(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "test.expected",
            message));

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        public ConcurrentQueue<Exception> Exceptions { get; } = [];

        public TaskCompletionSource<Exception> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    callback(state);
                }
                catch (Exception exception)
                {
                    Exceptions.Enqueue(exception);
                    Failure.TrySetResult(exception);
                }
            });
        }
    }
}
