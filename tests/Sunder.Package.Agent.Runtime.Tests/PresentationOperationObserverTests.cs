extern alias AgentCore;

using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.Input;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Xunit;
using PresentationOperation = AgentCore::Sunder.Package.Agent.Shared.Presentation.PresentationOperation;
using PresentationExceptionPolicy = AgentCore::Sunder.Package.Agent.Shared.Presentation.PresentationExceptionPolicy;
using PresentationOperationObserver = AgentCore::Sunder.Package.Agent.Shared.Presentation.PresentationOperationObserver;

namespace Sunder.Package.Agent.Tests;

public sealed class PresentationOperationObserverTests
{
    private static readonly PresentationOperation Operation = new(
        "test.operation",
        "The test operation could not be completed.");

    [Theory]
    [InlineData(SunderRpcErrorKind.ProviderFaulted)]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.NotFound)]
    [InlineData(SunderRpcErrorKind.ResourceExhausted)]
    public void ExceptionPolicy_ClassifiesUserOperationalRpcFailures(SunderRpcErrorKind kind)
    {
        var failure = RpcFailure(kind, "operational failure");

        Assert.True(PresentationExceptionPolicy.IsExpectedOperationFailure(failure));
    }

    [Fact]
    public void ExceptionPolicy_DoesNotClassifyProtocolOrProgrammingFailuresAsExpected()
    {
        Assert.False(PresentationExceptionPolicy.IsExpectedOperationFailure(
            RpcFailure(SunderRpcErrorKind.Protocol, "protocol failure")));
        Assert.False(PresentationExceptionPolicy.IsExpectedOperationFailure(
            new InvalidOperationException("invariant")));
    }

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
    public async Task RunAsync_PropagatedCancellationRemainsCallerObservable()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        using var observer = PresentationOperationObserver.CreateUnreported("test");
        var operation = Operation with { PropagateCancellation = true };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => observer.RunAsync(
            operation,
            _ => Task.FromCanceled(cancellation.Token)));
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

    [Fact]
    public async Task RunAsync_ProtocolFailureRemainsObservable()
    {
        using var observer = PresentationOperationObserver.CreateUnreported("test");
        var failure = RpcFailure(SunderRpcErrorKind.Protocol, "protocol invariant");

        var thrown = await Assert.ThrowsAsync<SunderRpcException>(() => observer.RunAsync(
            Operation,
            _ => Task.FromException(failure)));

        Assert.Same(failure, thrown);
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
    public async Task RunAsync_ReportingAndExpectedReconciliationFailuresAreObservedWithoutRecursion()
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
                return Task.FromException(new IOException("reconciliation failed"));
            });

        Assert.True(loggingAttempts >= 3);
        Assert.Equal(1, notificationAttempts);
        Assert.Equal(1, reconciliationAttempts);
    }

    [Fact]
    public async Task RunAsync_UnexpectedReconciliationFailureRemainsObservable()
    {
        var failure = new InvalidOperationException("reconciliation invariant");
        using var observer = PresentationOperationObserver.CreateUnreported("test");

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => observer.RunAsync(
            Operation,
            _ => Task.FromException(ExpectedFailure("operation failed")),
            _ => Task.FromException(failure)));

        Assert.Same(failure, thrown);
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
    public async Task Submit_UnexpectedFailureIsPostedOnceToCapturedSynchronizationContext()
    {
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var failure = new InvalidOperationException("submit invariant");
        var observer = PresentationOperationObserver.CreateUnreported("test");
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            observer.Submit(Operation, _ => Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var observed = await context.Failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await observer.DisposeAsync();

        Assert.Same(failure, observed);
        Assert.Equal(1, context.PostCount);
        Assert.Single(context.Exceptions);
    }

    [Fact]
    public async Task SubmitAsync_UnexpectedFailureIsReturnedWithoutDuplicateReporting()
    {
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var logs = new ConcurrentQueue<string>();
        var failure = new InvalidOperationException("awaited submit invariant");
        var observer = new PresentationOperationObserver(
            "test",
            (_, eventName, _, _, _) =>
            {
                logs.Enqueue(eventName);
                return ValueTask.CompletedTask;
            },
            publishNotification: null);
        Task submission;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            submission = observer.SubmitAsync(Operation, _ => Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => submission);
        await observer.DisposeAsync();

        Assert.Same(failure, observed);
        Assert.Equal(["presentation.operation.unexpected_failure"], logs.ToArray());
        Assert.Equal(0, context.PostCount);
        Assert.Empty(context.Exceptions);
    }

    [Fact]
    public async Task Submit_ExpectedHandledFailureIsNotPosted()
    {
        var context = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var notificationPublished = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = new PresentationOperationObserver(
            "test",
            writeEvent: null,
            _ =>
            {
                notificationPublished.TrySetResult();
                return ValueTask.CompletedTask;
            });
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            observer.Submit(Operation, _ => Task.FromException(ExpectedFailure("expected")));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await notificationPublished.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await observer.DisposeAsync();

        Assert.Equal(0, context.PostCount);
        Assert.Empty(context.Exceptions);
    }

    [Fact]
    public async Task SubmitAsync_UnknownFailureIsObservableWithoutSynchronizationContext()
    {
        var previous = SynchronizationContext.Current;
        var failure = new InvalidOperationException("invariant");
        Task submission;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            using var observer = PresentationOperationObserver.CreateUnreported("test");

            submission = observer.SubmitAsync(Operation, async _ =>
            {
                await Task.Yield();
                throw failure;
            });
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(
            () => submission.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task DisposeAsync_CancelsAndDrainsRunAsyncOperation()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var observer = PresentationOperationObserver.CreateUnreported("test");
        var submission = observer.RunAsync(Operation, async cancellationToken =>
        {
            started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult();
                throw;
            }
        });

        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await observer.DisposeAsync();

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await submission.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static SunderRpcException ExpectedFailure(string message)
        => RpcFailure(SunderRpcErrorKind.Domain, message);

    private static SunderRpcException RpcFailure(SunderRpcErrorKind kind, string message)
        => new(new SunderRpcError(kind, "test.failure", message));

    private sealed class RecordingSynchronizationContext : SynchronizationContext
    {
        private int _postCount;

        public ConcurrentQueue<Exception> Exceptions { get; } = [];

        public TaskCompletionSource<Exception> Failure { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int PostCount => Volatile.Read(ref _postCount);

        public override void Post(SendOrPostCallback callback, object? state)
        {
            Interlocked.Increment(ref _postCount);
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
