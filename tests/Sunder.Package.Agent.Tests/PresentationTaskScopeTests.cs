extern alias AgentCore;

using System.Collections.Concurrent;
using Xunit;
using PresentationTaskScope = AgentCore::Sunder.Package.Agent.Shared.Presentation.PresentationTaskScope;

namespace Sunder.Package.Agent.Tests;

public sealed class PresentationTaskScopeTests
{
    [Fact]
    public async Task Run_BlockedSynchronousPrefixDoesNotBlockAnotherRun()
    {
        var scope = new PresentationTaskScope();
        using var firstEntered = new ManualResetEventSlim();
        using var releaseFirst = new ManualResetEventSlim();
        var firstRun = Task.Run(() => scope.Run(_ =>
        {
            firstEntered.Set();
            releaseFirst.Wait();
            return Task.CompletedTask;
        }));
        var secondRun = Task.CompletedTask;
        var secondInvocationCount = 0;

        try
        {
            Assert.True(firstEntered.Wait(TimeSpan.FromSeconds(2)));
            secondRun = Task.Run(() => scope.Run(_ =>
            {
                Interlocked.Increment(ref secondInvocationCount);
                return Task.CompletedTask;
            }));

            await secondRun.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.Equal(1, Volatile.Read(ref secondInvocationCount));
            Assert.False(firstRun.IsCompleted);
        }
        finally
        {
            releaseFirst.Set();
            await Task.WhenAll(firstRun, secondRun).WaitAsync(TimeSpan.FromSeconds(2));
            scope.Dispose();
        }
    }

    [Fact]
    public async Task Dispose_BlockedAcceptedPrefixCompletesAndSuppressesItsCancellation()
    {
        var failureCount = 0;
        var scope = new PresentationTaskScope(_ =>
        {
            Interlocked.Increment(ref failureCount);
            return Task.CompletedTask;
        });
        using var operationEntered = new ManualResetEventSlim();
        using var releaseOperation = new ManualResetEventSlim();
        var operationContinued = 0;
        var run = Task.Run(() => scope.Run(cancellationToken =>
        {
            operationEntered.Set();
            releaseOperation.Wait();
            Interlocked.Exchange(ref operationContinued, 1);
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }));
        var dispose = Task.CompletedTask;

        try
        {
            Assert.True(operationEntered.Wait(TimeSpan.FromSeconds(2)));
            dispose = Task.Run(scope.Dispose);

            await dispose.WaitAsync(TimeSpan.FromSeconds(2));

            Assert.True(scope.CancellationToken.IsCancellationRequested);
        }
        finally
        {
            releaseOperation.Set();
            await Task.WhenAll(run, dispose).WaitAsync(TimeSpan.FromSeconds(2));
            scope.Dispose();
        }

        Assert.Equal(1, Volatile.Read(ref operationContinued));
        Assert.Equal(0, Volatile.Read(ref failureCount));
    }

    [Fact]
    public void Run_InvokesSynchronousPrefixOnCallingThread()
    {
        using var scope = new PresentationTaskScope();
        var callingThreadId = Environment.CurrentManagedThreadId;
        var invocationThreadId = -1;
        var invokedBeforeReturn = false;

        scope.Run(_ =>
        {
            invocationThreadId = Environment.CurrentManagedThreadId;
            invokedBeforeReturn = true;
            return Task.CompletedTask;
        });

        Assert.True(invokedBeforeReturn);
        Assert.Equal(callingThreadId, invocationThreadId);
    }

    [Fact]
    public async Task Run_ObservesSynchronousAndAsynchronousFailuresOnce()
    {
        var synchronousFailure = new InvalidOperationException("Synchronous failure.");
        var asynchronousFailure = new InvalidOperationException("Asynchronous failure.");
        var observedFailures = new ConcurrentQueue<Exception>();
        var releaseHandlers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothHandlersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothHandlersCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntryCount = 0;
        var handlerCompletionCount = 0;
        using var scope = new PresentationTaskScope(HandleFailureAsync);

        scope.Run(_ => throw synchronousFailure);
        scope.Run(async _ =>
        {
            await Task.Yield();
            throw asynchronousFailure;
        });

        Exception[] failures = [];
        try
        {
            await bothHandlersEntered.Task.WaitAsync(TimeSpan.FromSeconds(2));
            failures = observedFailures.ToArray();
        }
        finally
        {
            releaseHandlers.TrySetResult();
        }

        await bothHandlersCompleted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(2, Volatile.Read(ref handlerEntryCount));
        Assert.Equal(2, failures.Length);
        Assert.Single(failures, failure => ReferenceEquals(failure, synchronousFailure));
        Assert.Single(failures, failure => ReferenceEquals(failure, asynchronousFailure));

        async Task HandleFailureAsync(Exception exception)
        {
            observedFailures.Enqueue(exception);
            if (Interlocked.Increment(ref handlerEntryCount) == 2)
            {
                bothHandlersEntered.TrySetResult();
            }

            await releaseHandlers.Task;
            if (Interlocked.Increment(ref handlerCompletionCount) == 2)
            {
                bothHandlersCompleted.TrySetResult();
            }
        }
    }

    [Fact]
    public void Run_AfterDisposeIsRejectedAndDisposeIsIdempotent()
    {
        var scope = new PresentationTaskScope();
        var invocationCount = 0;

        scope.Dispose();
        scope.Dispose();
        scope.Run(_ =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.CompletedTask;
        });

        Assert.Equal(0, Volatile.Read(ref invocationCount));
    }

    [Fact]
    public async Task Run_WithoutFailureHandlerForwardsFailureToSynchronizationContext()
    {
        var context = new FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var failure = new InvalidOperationException("unhandled presentation task");
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            using var scope = new PresentationTaskScope();

            scope.Run(async _ =>
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

    private sealed class FailureRecordingSynchronizationContext : SynchronizationContext
    {
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
                    Failure.TrySetResult(exception);
                }
            });
        }
    }
}
