using Avalonia.Threading;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView
{
    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var generation = Interlocked.Increment(ref _navigationGeneration);
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var navigationToken = navigationCancellation.Token;
        var previousCancellation = Interlocked.Exchange(
            ref _navigationCancellation,
            navigationCancellation);
        try
        {
            previousCancellation?.Cancel();
            await Dispatcher.UIThread.InvokeAsync(
                () => OnNavigatedToCoreAsync(generation, navigationToken),
                DispatcherPriority.Background);
        }
        finally
        {
            Interlocked.CompareExchange(ref _navigationCancellation, null, navigationCancellation);
            navigationCancellation.Dispose();
        }
    }

    private async Task OnNavigatedToCoreAsync(int generation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed
            || generation != Volatile.Read(ref _navigationGeneration)
            || ViewModel is not { } viewModel)
        {
            return;
        }

        var wasInitialized = viewModel.IsInitialized;
        var displayedTranscriptSessionId = viewModel.DisplayedTranscriptSessionId;
        if (!wasInitialized)
        {
            _transcriptBehavior.MarkInitialPlacementPending(cancellationToken);
        }
        try
        {
            await viewModel.InitializeAsync(cancellationToken);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            viewModel.ReportStartupFailure();
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _navigationGeneration))
        {
            return;
        }

        if (wasInitialized
            && displayedTranscriptSessionId == viewModel.DisplayedTranscriptSessionId)
        {
            return;
        }

        if (wasInitialized)
        {
            _transcriptBehavior.MarkInitialPlacementPending(cancellationToken);
        }

        _transcriptBehavior.OnTranscriptChanged();
        await _transcriptBehavior.WaitForInitialPresentationAsync(cancellationToken);
    }
}
