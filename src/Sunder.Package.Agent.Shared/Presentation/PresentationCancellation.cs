namespace Sunder.Package.Agent.Shared.Presentation;

// Shared with Worker roles: cancellation classification must not initialize Avalonia.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal static class PresentationCancellation
{
    public static bool IsExpected(Exception exception, CancellationToken ownerToken)
        => exception is OperationCanceledException cancellation
           && !PresentationExceptionPolicy.IsFatal(exception)
           && (exception is PresentationDispatchCanceledException
               {
                   Reason: PresentationDispatchCancellationReason.DispatcherShutdown
                       or PresentationDispatchCancellationReason.DispatcherAborted,
               }
                ? cancellation.CancellationToken.IsCancellationRequested
                : ownerToken.IsCancellationRequested && cancellation.CancellationToken == ownerToken);

    public static void ThrowIfStopped(CancellationToken ownerToken, CancellationToken shutdownToken)
    {
        if (ownerToken.IsCancellationRequested)
            throw new PresentationDispatchCanceledException(PresentationDispatchCancellationReason.OwnerStopped, ownerToken);
        if (shutdownToken.IsCancellationRequested)
            throw new PresentationDispatchCanceledException(PresentationDispatchCancellationReason.DispatcherShutdown, shutdownToken);
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal enum PresentationDispatchCancellationReason { OwnerStopped, DispatcherShutdown, DispatcherAborted }

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class PresentationDispatchCanceledException(
    PresentationDispatchCancellationReason reason, CancellationToken token, Exception? inner = null)
    : OperationCanceledException($"Presentation dispatch stopped: {reason}.", inner, token)
{
    public PresentationDispatchCancellationReason Reason { get; } = reason;
}

// A callback that ran and threw is not a dispatcher abort, even if shutdown raced its failure.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
internal sealed class PresentationCallbackException(Exception inner)
    : Exception("A presentation callback failed after it started.", inner);
