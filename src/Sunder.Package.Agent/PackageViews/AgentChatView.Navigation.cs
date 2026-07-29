using Avalonia.Threading;
using Sunder.Sdk.Abstractions;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentChatView
{
    public async ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        if (await PrepareNavigationAsync(context, cancellationToken))
        {
            await OnNavigationPresentedAsync(context, cancellationToken);
        }
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var target = HistorySearchNavigation.Parse(context.Parameters);
        var generation = Interlocked.Increment(ref _navigationGeneration);
        var navigationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var navigationToken = navigationCancellation.Token;
        var previousCancellation = Interlocked.Exchange(
            ref _navigationCancellation,
            navigationCancellation);
        try
        {
            previousCancellation?.Cancel();
            return await Dispatcher.UIThread.InvokeAsync(
                () => OnNavigatedToCoreAsync(generation, target, navigationToken),
                DispatcherPriority.Background);
        }
        finally
        {
            Interlocked.CompareExchange(ref _navigationCancellation, null, navigationCancellation);
            navigationCancellation.Dispose();
        }
    }

    public async ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var prepared = await Dispatcher.UIThread.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_preparedHistoryNavigation is not { } navigation
                || navigation.ViewGeneration != Volatile.Read(ref _navigationGeneration))
            {
                return null;
            }

            _preparedHistoryNavigation = null;
            return navigation;
        }, DispatcherPriority.Background);
        if (prepared is not null && ViewModel is { } viewModel)
        {
            await viewModel.AcknowledgeHistoryPresentationAsync(
                prepared.Placement,
                cancellationToken);
        }
    }

    private async Task<bool> OnNavigatedToCoreAsync(
        int generation,
        HistorySearchNavigationTarget? target,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_disposed
            || generation != Volatile.Read(ref _navigationGeneration)
            || ViewModel is not { } viewModel)
        {
            return false;
        }

        _preparedHistoryNavigation = null;
        var wasInitialized = viewModel.IsInitialized;
        var displayedTranscriptSessionId = viewModel.DisplayedTranscriptSessionId;
        var usesLoadedHistoryWindow = target is not null
            && viewModel.TryResolveLoadedHistoryAnchor(target, out _);
        if (!wasInitialized || target is not null)
        {
            object? preliminaryAnchor = target is null
                ? null
                : target.AnchorKind == HistoryAnchorKind.Text
                    ? TranscriptRowAnchorKey.Text(target.TurnId)
                    : new TranscriptRowAnchorKey($"tool:{target.TurnId:N}:{target.ItemId:N}");
            _transcriptBehavior.MarkInitialPlacementPending(
                cancellationToken,
                preliminaryAnchor,
                hideTranscript: !usesLoadedHistoryWindow);
        }
        try
        {
            if (target is null)
            {
                await viewModel.InitializeAsync(cancellationToken);
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            viewModel.ReportStartupFailure(exception);
            return false;
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (generation != Volatile.Read(ref _navigationGeneration))
        {
            return false;
        }

        if (target is not null)
        {
            try
            {
                var placement = await viewModel.NavigateToHistoryAnchorAsync(
                    target,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (generation != Volatile.Read(ref _navigationGeneration))
                {
                    return false;
                }
                viewModel.EnsureCurrentHistoryPlacement(target, placement, cancellationToken);
                _transcriptBehavior.SetInitialPlacementAnchor(
                    placement.AnchorKey,
                    cancellationToken,
                    hideTranscript: !placement.UsesLoadedWindow);
                _transcriptBehavior.OnTranscriptChanged();
                await _transcriptBehavior.WaitForInitialPresentationAsync(cancellationToken);
                _preparedHistoryNavigation = new PreparedAgentHistoryNavigation(
                    generation,
                    placement);
                return true;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (AgentHistoryTargetUnavailableException)
            {
                await RestoreRejectedNavigationPresentationAsync(cancellationToken);
                return false;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                viewModel.ReportTranscriptPagingFailure(exception);
                await RestoreRejectedNavigationPresentationAsync(cancellationToken);
                return false;
            }
        }

        if (wasInitialized
            && displayedTranscriptSessionId == viewModel.DisplayedTranscriptSessionId)
        {
            return true;
        }

        if (wasInitialized)
        {
            _transcriptBehavior.MarkInitialPlacementPending(cancellationToken);
        }

        _transcriptBehavior.OnTranscriptChanged();
        await _transcriptBehavior.WaitForInitialPresentationAsync(cancellationToken);
        return true;
    }

    private async Task RestoreRejectedNavigationPresentationAsync(
        CancellationToken cancellationToken)
    {
        _transcriptBehavior.MarkInitialPlacementPending(cancellationToken);
        _transcriptBehavior.OnTranscriptChanged();
        await _transcriptBehavior.WaitForInitialPresentationAsync(cancellationToken);
    }

    private sealed record PreparedAgentHistoryNavigation(
        int ViewGeneration,
        AgentHistoryNavigationPlacement Placement);
}
