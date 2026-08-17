using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubsessionsViewModel
{
    private static readonly PresentationOperation SubsessionNavigationOperation = new(
        "subsessions.navigate",
        "The subsession could not be opened. The subsession list was refreshed.");
    private static readonly PresentationOperation SubsessionPreparationOperation = new(
        "subsessions.prepare-navigation",
        "The subsession could not be opened. The subsession list was refreshed.",
        NotificationKey: "subsessions.navigate",
        PropagateCancellation: true);
    private static readonly PresentationOperation SubsessionRefreshOperation = new(
        "subsessions.refresh",
        "Subsessions could not refresh. The current presentation was preserved.");

    private Task ReconcileSubsessionsAsync(CancellationToken cancellationToken)
        => ReloadSubsessionsAsync(
            SelectedSubsession?.SessionId,
            cancellationToken);

    private Task ReconcileSubsessionLoadFailureAsync(
        Exception exception,
        CancellationToken cancellationToken)
    {
        var failure = exception.InnerException ?? exception;
        return RunOnUiThreadAsync(() =>
        {
            if (!_disposed)
            {
                SetLoadFailure(failure.Message);
            }
        }, cancellationToken);
    }

    private static bool IsExpectedSubsessionRuntimeFailure(Exception exception)
        => !PresentationExceptionPolicy.IsFatal(exception)
           && (PresentationExceptionPolicy.IsExpectedOperationFailure(exception)
               || exception is InvalidOperationException or InvalidDataException);
}
