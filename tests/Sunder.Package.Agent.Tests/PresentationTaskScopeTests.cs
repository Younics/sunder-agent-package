extern alias AgentCore;

using System.Collections.Concurrent;
using Sunder.Sdk.Rpc;
using Xunit;
using PresentationTaskScope = AgentCore::Sunder.Package.Agent.Shared.Presentation.PresentationTaskScope;
using AgentCore::Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Tests;

public sealed class PresentationTaskScopeTests
{
    [Fact]
    public async Task Run_BlockedSynchronousPrefixDoesNotBlockAnotherRun()
    {
        var scope = new PresentationTaskScope(PresentationTaskScope.PropagateExpectedFailure);
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
            dispose = scope.DisposeAsync().AsTask();

            await WaitUntilAsync(() => scope.CancellationToken.IsCancellationRequested);

            Assert.True(scope.CancellationToken.IsCancellationRequested);
            Assert.False(dispose.IsCompleted);
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
        using var scope = new PresentationTaskScope(PresentationTaskScope.PropagateExpectedFailure);
        var callingThreadId = Environment.CurrentManagedThreadId;
        var invocationThreadId = -1;
        var invokedBeforeReturn = false;

        var observation = scope.RunAsync(_ =>
        {
            invocationThreadId = Environment.CurrentManagedThreadId;
            invokedBeforeReturn = true;
            return Task.CompletedTask;
        });

        Assert.True(invokedBeforeReturn);
        Assert.Equal(callingThreadId, invocationThreadId);
        Assert.True(observation.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Run_ObservesExpectedSynchronousAndAsynchronousFailuresOnce()
    {
        var synchronousFailure = new IOException("Synchronous failure.");
        var asynchronousFailure = new IOException("Asynchronous failure.");
        var observedFailures = new ConcurrentQueue<Exception>();
        var releaseHandlers = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothHandlersEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothHandlersCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handlerEntryCount = 0;
        var handlerCompletionCount = 0;
        using var scope = new PresentationTaskScope(HandleFailureAsync);

        var synchronousObservation = scope.RunAsync(_ => throw synchronousFailure);
        var asynchronousObservation = scope.RunAsync(async _ =>
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
        await Task.WhenAll(synchronousObservation, asynchronousObservation);

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
    public async Task Run_UnexpectedFailureIsPostedOnceToCapturedSynchronizationContext()
    {
        var context = new FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var failure = new InvalidOperationException("unhandled presentation task");
        var scope = new PresentationTaskScope(PresentationTaskScope.PropagateExpectedFailure);
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            scope.Run(_ => Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var observed = await context.Failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scope.DisposeAsync();

        Assert.Same(failure, observed);
        Assert.Equal(1, context.PostCount);
        Assert.Single(context.Exceptions);
    }

    [Fact]
    public async Task RunAsync_UnexpectedFailureIsReturnedWithoutPosting()
    {
        var context = new FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var failure = new InvalidOperationException("caller-owned presentation task");
        var scope = new PresentationTaskScope(PresentationTaskScope.PropagateExpectedFailure);
        Task observation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            observation = scope.RunAsync(_ => Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() => observation);
        await scope.DisposeAsync();

        Assert.Same(failure, observed);
        Assert.Equal(0, context.PostCount);
        Assert.Empty(context.Exceptions);
    }

    [Fact]
    public async Task Run_HandledFailureIsNotPosted()
    {
        var context = new FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var handled = new ConcurrentQueue<Exception>();
        var failure = new IOException("handled presentation failure");
        var scope = new PresentationTaskScope(exception =>
        {
            handled.Enqueue(exception);
            return Task.CompletedTask;
        });
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            scope.Run(_ => Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        await scope.DisposeAsync();

        Assert.Same(failure, Assert.Single(handled));
        Assert.Equal(0, context.PostCount);
        Assert.Empty(context.Exceptions);
    }

    [Fact]
    public async Task Run_InvalidOperationExceptionBypassesHandlerAndIsPosted()
    {
        var context = new FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var handlerInvocationCount = 0;
        var failure = new InvalidOperationException("programming failure");
        var scope = new PresentationTaskScope(_ =>
        {
            Interlocked.Increment(ref handlerInvocationCount);
            return Task.CompletedTask;
        });
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            scope.Run(_ => Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var observed = await context.Failure.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await scope.DisposeAsync();

        Assert.Same(failure, observed);
        Assert.Equal(0, Volatile.Read(ref handlerInvocationCount));
        Assert.Equal(1, context.PostCount);
        Assert.Single(context.Exceptions);
    }

    [Fact]
    public async Task RunAsync_ProtocolFailureBypassesHandlerAndIsReturned()
    {
        var context = new FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var handlerInvocationCount = 0;
        var failure = new SunderRpcException(
            new SunderRpcError(SunderRpcErrorKind.Protocol, "test.protocol", "protocol failure"));
        var scope = new PresentationTaskScope(_ =>
        {
            Interlocked.Increment(ref handlerInvocationCount);
            return Task.CompletedTask;
        });
        Task observation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            observation = scope.RunAsync(_ => Task.FromException(failure));
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }

        var observed = await Assert.ThrowsAsync<SunderRpcException>(() => observation);
        await scope.DisposeAsync();

        Assert.Same(failure, observed);
        Assert.Equal(0, Volatile.Read(ref handlerInvocationCount));
        Assert.Equal(0, context.PostCount);
        Assert.Empty(context.Exceptions);
    }

    [Fact]
    public async Task RunAsync_FatalFailureBypassesHandlerAndIsReturned()
    {
        var handlerInvocationCount = 0;
        var failure = new OutOfMemoryException("fatal presentation failure");
        using var scope = new PresentationTaskScope(_ =>
        {
            Interlocked.Increment(ref handlerInvocationCount);
            return Task.CompletedTask;
        });

        var observed = await Assert.ThrowsAsync<OutOfMemoryException>(() =>
            scope.RunAsync(_ => Task.FromException(failure)));

        Assert.Same(failure, observed);
        Assert.Equal(0, Volatile.Read(ref handlerInvocationCount));
    }

    [Fact]
    public void Run_AfterDisposeIsRejectedAndDisposeIsIdempotent()
    {
        var scope = new PresentationTaskScope(PresentationTaskScope.PropagateExpectedFailure);
        var invocationCount = 0;

        scope.Dispose();
        scope.Dispose();
        var rejected = scope.RunAsync(_ =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.CompletedTask;
        });

        Assert.Equal(0, Volatile.Read(ref invocationCount));
        Assert.True(rejected.IsCompletedSuccessfully);
    }

    [Fact]
    public void Track_AfterDisposeDoesNotStartOperation()
    {
        var scope = new PresentationTaskScope(PresentationTaskScope.PropagateExpectedFailure);
        var invocationCount = 0;
        scope.Dispose();

        var rejected = scope.Track(_ =>
        {
            Interlocked.Increment(ref invocationCount);
            return Task.CompletedTask;
        });

        Assert.Equal(0, Volatile.Read(ref invocationCount));
        Assert.True(rejected.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task RunAsync_WithoutSynchronizationContextReturnsObservableFailure()
    {
        var previous = SynchronizationContext.Current;
        var failure = new InvalidOperationException("unhandled presentation task");
        Task observation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(null);
            using var scope = new PresentationTaskScope(PresentationTaskScope.PropagateExpectedFailure);

            observation = scope.RunAsync(async _ =>
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
            () => observation.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.Same(failure, observed);
    }

    [Fact]
    public async Task Run_FailureHandlerFailureRemainsObservable()
    {
        var operationFailure = new IOException("operation failed");
        var handlerFailure = new InvalidOperationException("failure handler failed");
        using var scope = new PresentationTaskScope(
            _ => Task.FromException(handlerFailure));

        var observed = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.RunAsync(_ => Task.FromException(operationFailure)));

        Assert.Same(handlerFailure, observed);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task QueuedDispatch_StopDuringOperationOrFailureHandlerDrainsWithoutUnhandledPost(bool shutdown, bool inHandler)
    {
        using var dispatcher = new QueuedPresentationDispatcher();
        var context = new FailureRecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        var callbacks = 0;
        var handlers = 0;
        var scope = new PresentationTaskScope((_, token) =>
        {
            handlers++;
            return ((IPresentationDispatcher)dispatcher).InvokeAsync(() => callbacks++, token);
        }, dispatcher.ShutdownToken);
        Task observation;
        try
        {
            SynchronizationContext.SetSynchronizationContext(context);
            observation = scope.Track(token => inHandler
                ? Task.FromException(new IOException("original operation failure"))
                : ((IPresentationDispatcher)dispatcher).InvokeAsync(() => callbacks++, token));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }

        await dispatcher.Queued.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (shutdown) dispatcher.Shutdown();
        var disposal = scope.DisposeAsync().AsTask();
        if (!shutdown) dispatcher.Execute();
        await Task.WhenAll(observation, disposal).WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(inHandler ? 1 : 0, handlers);
        Assert.Equal(0, callbacks);
        Assert.Equal(0, context.PostCount);
        Assert.Empty(context.Exceptions);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ForeignOrTokenlessCancellationRemainsObservableAfterOwnerStop(bool inHandler, bool foreignToken)
    {
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var failure = new OperationCanceledException(foreignToken ? foreign.Token : default);
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new PresentationTaskScope(_ => pending.Task);
        var observation = scope.RunAsync(_ => inHandler ? Task.FromException(new IOException("original")) : pending.Task);
        var disposal = scope.DisposeAsync().AsTask();
        pending.SetException(failure);
        Assert.Same(failure, await Assert.ThrowsAsync<OperationCanceledException>(() => observation));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ForeignDispatchOwnerCancellationRemainsObservableAfterScopeStop(bool inHandler)
    {
        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var failure = Assert.Throws<PresentationDispatchCanceledException>(() =>
            PresentationCancellation.ThrowIfStopped(foreign.Token, default));
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new PresentationTaskScope(_ => pending.Task);
        var observation = scope.RunAsync(_ => inHandler ? Task.FromException(new IOException("original")) : pending.Task);
        var disposal = scope.DisposeAsync().AsTask();

        pending.SetException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<PresentationDispatchCanceledException>(() => observation));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FailureHandlerRealOrFatalErrorRemainsObservableAfterOwnerStop(bool fatal)
    {
        Exception failure = fatal ? new OutOfMemoryException("handler fatal") : new InvalidOperationException("handler bug");
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var scope = new PresentationTaskScope(_ => pending.Task);
        var observation = scope.RunAsync(_ => Task.FromException(new IOException("original")));
        var disposal = scope.DisposeAsync().AsTask();
        pending.SetException(failure);
        Assert.Same(failure, await Record.ExceptionAsync(() => observation));
        await disposal.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData("cancellation")]
    [InlineData("operation")]
    [InlineData("fatal")]
    public async Task CallbackThatActuallyRanCannotBeMistakenForOwnerOrDispatcherCancellation(string failureKind)
    {
        using var dispatcher = new QueuedPresentationDispatcher();
        PresentationTaskScope scope = null!;
        Exception? callbackFailure = null;
        scope = new PresentationTaskScope((_, token) => ((IPresentationDispatcher)dispatcher).InvokeAsync(() =>
        {
            scope.Dispose();
            callbackFailure = failureKind switch
            {
                "cancellation" => new OperationCanceledException(token),
                "operation" => new IOException("Callback failed after owner stop."),
                _ => new OutOfMemoryException("Fatal callback failure."),
            };
            throw callbackFailure;
        }, token));
        var observation = scope.RunAsync(_ => Task.FromException(new IOException("original")));
        dispatcher.Execute();
        var failure = await Record.ExceptionAsync(() => observation);
        if (failureKind == "cancellation")
        {
            Assert.Same(callbackFailure, Assert.IsType<PresentationCallbackException>(failure).InnerException);
        }
        else
        {
            Assert.Same(callbackFailure, failure);
        }
        await scope.DisposeAsync();
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        while (!condition())
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    internal sealed class FailureRecordingSynchronizationContext : SynchronizationContext
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

// A paused UI queue: tests choose whether the callback ran or was aborted. No shared UIThread shutdown.
internal sealed class QueuedPresentationDispatcher : IPresentationDispatcher, IDisposable
{
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private Action? _action;
    public TaskCompletionSource Queued { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public CancellationToken ShutdownToken => _shutdown.Token;
    public bool CheckAccess() => false;
    public Task InvokeAsync(Action action)
    {
        _action = action;
        Queued.TrySetResult();
        return _completion.Task;
    }
    public void Execute()
    {
        try { _action!(); _completion.TrySetResult(); }
        catch (Exception error) { _completion.TrySetException(error); }
    }
    public void Shutdown()
    {
        _shutdown.Cancel();
        _completion.TrySetException(new PresentationDispatchCanceledException(
            PresentationDispatchCancellationReason.DispatcherShutdown, ShutdownToken));
    }
    public void Dispose() => _shutdown.Dispose();
}
