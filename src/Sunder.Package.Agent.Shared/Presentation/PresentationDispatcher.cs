using Avalonia;
using Avalonia.Threading;
using System.Runtime.CompilerServices;

namespace Sunder.Package.Agent.Shared.Presentation;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal interface IPresentationDispatcher
{
    bool CheckAccess();

    Task InvokeAsync(Action action);

    CancellationToken ShutdownToken => default;

    Task InvokeAsync(Action action, CancellationToken cancellationToken)
        => PresentationDispatcher.InvokeLegacyAsync(this, action, cancellationToken);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal static class PresentationDispatcher
{
    // A live native key keeps a ConditionalWeakTable value alive through a dependent handle.
    // A package-defined value would therefore pin its collectible assembly (and this static
    // table) even after every owner retires. Cache only BCL state; adapters remain owner-scoped.
    private static readonly ConditionalWeakTable<Dispatcher, CancellationTokenSource> ShutdownSources = new();

    public static IPresentationDispatcher Capture()
        => Application.Current is null ? ImmediatePresentationDispatcher.Instance : For(Dispatcher.UIThread);

    internal static IPresentationDispatcher For(Dispatcher dispatcher)
        => CreateNative(dispatcher);

    private static AvaloniaPresentationDispatcher CreateNative(Dispatcher dispatcher)
        => new(dispatcher, ShutdownSources.GetValue(dispatcher, static _ => new()));

    public static Task QueueAsync(Action action, DispatcherPriority priority, CancellationToken cancellationToken)
        => CreateNative(Dispatcher.UIThread)
            .InvokeAsync(action, cancellationToken, priority);

    internal static async Task InvokeLegacyAsync(IPresentationDispatcher dispatcher, Action action, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(action);
        PresentationCancellation.ThrowIfStopped(token, dispatcher.ShutdownToken);
        await dispatcher.InvokeAsync(() =>
        {
            PresentationCancellation.ThrowIfStopped(token, dispatcher.ShutdownToken);
            InvokeCallback(action);
        }).ConfigureAwait(false);
    }

    internal static void InvokeCallback(Action action)
    {
        try { action(); }
        // Only callback cancellation needs an origin marker so it cannot become stop proof.
        // Ordinary failures retain their identity for the owning operation's domain handlers.
        catch (OperationCanceledException error) { throw new PresentationCallbackException(error); }
    }

    // Non-Avalonia consumers/tests keep their existing immediate behavior. An actual native
    // dispatcher, including an explicitly supplied dispatcher, always queues its operation.
    private sealed class ImmediatePresentationDispatcher : IPresentationDispatcher
    {
        public static ImmediatePresentationDispatcher Instance { get; } = new();
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) => InvokeAsync(action, default);
        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(action);
                PresentationCancellation.ThrowIfStopped(cancellationToken, default);
                InvokeCallback(action);
                return Task.CompletedTask;
            }
            catch (Exception error) { return Task.FromException(error); }
        }
    }

    private sealed class AvaloniaPresentationDispatcher(Dispatcher dispatcher, CancellationTokenSource shutdown) : IPresentationDispatcher
    {
        private readonly Dispatcher _dispatcher = dispatcher;
        private readonly CancellationTokenSource _shutdown = shutdown;

        // Records shutdown observed while dispatch is pending. Activation owners independently
        // cancel their producers before draining; an idle adapter has no native event subscription.
        public CancellationToken ShutdownToken => _shutdown.Token;

        public bool CheckAccess() => _dispatcher.CheckAccess();

        public Task InvokeAsync(Action action) => InvokeAsync(action, default);

        public Task InvokeAsync(Action action, CancellationToken cancellationToken)
            => InvokeAsync(action, cancellationToken, DispatcherPriority.Background);

        public async Task InvokeAsync(Action action, CancellationToken cancellationToken, DispatcherPriority priority)
        {
            ArgumentNullException.ThrowIfNull(action);
            PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
            EventHandler shutdown = (_, _) => _shutdown.Cancel();
            // The native dispatcher outlives collectible package assemblies. Its event may retain
            // this adapter only for the duration of a pending operation, never for the capture.
            _dispatcher.ShutdownStarted += shutdown;
            try
            {
                PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
                var started = 0;
                var operation = _dispatcher.InvokeAsync(() =>
                {
                    PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
                    Volatile.Write(ref started, 1);
                    InvokeCallback(action);
                }, priority);
                using var ownerRegistration = cancellationToken.Register(() => operation.Abort());
                using var shutdownRegistration = ShutdownToken.Register(() => operation.Abort());
                try
                {
                    await operation.GetTask().ConfigureAwait(false);
                }
                catch (OperationCanceledException error) when (Volatile.Read(ref started) == 0
                                                              && operation.Status == DispatcherOperationStatus.Aborted
                                                              && !PresentationExceptionPolicy.IsFatal(error))
                {
                    PresentationCancellation.ThrowIfStopped(cancellationToken, ShutdownToken);
                    // This also covers captures after shutdown, even on the dispatcher thread.
                    // An aborted operation proves non-execution, not a global shutdown: owner-only
                    // Abort must never poison ShutdownToken for subsequent callers.
                    throw new PresentationDispatchCanceledException(
                        PresentationDispatchCancellationReason.DispatcherAborted, new CancellationToken(true), error);
                }
            }
            finally
            {
                _dispatcher.ShutdownStarted -= shutdown;
            }
        }
    }
}
