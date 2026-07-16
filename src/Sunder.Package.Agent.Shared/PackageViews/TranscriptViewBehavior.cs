using Avalonia.Controls;
using Avalonia.Threading;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class TranscriptViewBehavior : IDisposable
{
    private readonly Control _owner;
    private readonly ScrollViewer _scrollViewer;
    private readonly Control _itemsControl;
    private readonly Button _jumpToLatestButton;
    private readonly Func<bool> _isInitialLoading;
    private readonly Func<bool> _hasRows;
    private readonly Func<bool> _hasTranscriptSelection;
    private readonly TranscriptScrollCoordinator _scrollCoordinator;
    private readonly PresentationTaskScope _tasks = new();
    private bool _changedBeforeScrollReady;
    private bool _initialPlacementPending = true;
    private bool _initialPlacementQueued;
    private bool _initialVisibilityRetryQueued;
    private int _initialPlacementVersion;
    private TaskCompletionSource _initialPresentation = CreatePresentationCompletion();
    private CancellationTokenSource? _initialPlacementCancellation;
    private bool _loaded;
    private bool _disposed;

    public TranscriptViewBehavior(
        Control owner,
        ScrollViewer scrollViewer,
        Control itemsControl,
        Button jumpToLatestButton,
        Func<bool> canLoadOlder,
        Func<object?, CancellationToken, Task<bool>> loadOlder,
        Func<bool> canLoadNewer,
        Func<object?, CancellationToken, Task<bool>> loadNewer,
        Func<bool> hasNewer,
        Func<bool> isInitialLoading,
        Func<bool> hasRows,
        Func<bool> hasTranscriptSelection,
        Action detachFromLatest,
        Action reachedLatest,
        Action<bool>? jumpVisibilityChanged = null,
        Action<TranscriptViewportAnchorData?>? viewportAnchorChanged = null,
        Action<Exception>? pagingFailed = null,
        Func<IEnumerable<(object Item, Control Visual)>>? enumerateRealizedAnchors = null,
        Func<object, Control?>? realizeAnchor = null)
    {
        _owner = owner;
        _scrollViewer = scrollViewer;
        _itemsControl = itemsControl;
        _jumpToLatestButton = jumpToLatestButton;
        _isInitialLoading = isInitialLoading;
        _hasRows = hasRows;
        _hasTranscriptSelection = hasTranscriptSelection;
        _scrollViewer.Opacity = 0;
        _scrollCoordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            itemsControl,
            canLoadOlder,
            loadOlder,
            canLoadNewer,
            loadNewer,
            hasNewer,
            isVisible =>
            {
                jumpToLatestButton.IsVisible = isVisible;
                jumpVisibilityChanged?.Invoke(isVisible);
            },
            detachFromLatest,
            reachedLatest,
            viewportAnchorChanged,
            pagingFailed,
            enumerateRealizedAnchors,
            realizeAnchor);
        _owner.Loaded += OnLoaded;
    }

    public void OnTranscriptChanging(bool isPaging)
    {
        if (_disposed || _initialPlacementPending || _isInitialLoading())
        {
            return;
        }

        if (isPaging)
        {
            _scrollCoordinator.DiscardPendingTranscriptMutation();
            return;
        }

        _scrollCoordinator.BeginTranscriptMutation();
    }

    public void OnTranscriptChanged()
    {
        if (_disposed)
        {
            return;
        }

        if (!_loaded)
        {
            _changedBeforeScrollReady = true;
            return;
        }

        if (!TryPlaceInitialTranscript())
        {
            _scrollCoordinator.OnTranscriptChanged();
        }
    }

    public void MarkInitialPlacementPending(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        _initialPlacementCancellation?.Cancel();
        _initialPlacementCancellation?.Dispose();
        _initialPlacementCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (!_initialPlacementPending || _initialPlacementQueued)
        {
            _initialPlacementVersion++;
        }

        if (_initialPresentation.Task.IsCompleted || _initialPlacementQueued)
        {
            _initialPresentation = CreatePresentationCompletion();
        }

        _initialPlacementPending = true;
        _initialPlacementQueued = false;
        _initialVisibilityRetryQueued = false;
        _scrollViewer.Opacity = 0;
    }

    public Task WaitForInitialPresentationAsync(CancellationToken cancellationToken = default)
        => _initialPresentation.Task.WaitAsync(cancellationToken);

    public void JumpToLatest(Action jumpToLatest)
    {
        if (_disposed)
        {
            return;
        }

        if (!_jumpToLatestButton.IsVisible)
        {
            _scrollCoordinator.QueueScrollToBottom(force: true);
            return;
        }

        _scrollCoordinator.ForceScrollToBottomOnNextTranscriptChanged();
        jumpToLatest();
    }

    public void MutateViewport(Action mutation)
    {
        if (_disposed)
        {
            return;
        }

        _scrollCoordinator.BeginViewportMutation();
        mutation();
        _scrollCoordinator.OnViewportContentChanged();
    }

    public void ScrollToBottom() => _scrollCoordinator.QueueScrollToBottom(force: true);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialPlacementCancellation?.Cancel();
        _initialPlacementCancellation?.Dispose();
        _initialPlacementCancellation = null;
        _initialPresentation.TrySetCanceled();
        _owner.Loaded -= OnLoaded;
        _tasks.Dispose();
        _scrollCoordinator.Dispose();
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        _loaded = true;
        HandleTranscriptReady();
    }

    private void HandleTranscriptReady()
    {
        if (_disposed || TryPlaceInitialTranscript())
        {
            return;
        }

        if (_changedBeforeScrollReady)
        {
            _changedBeforeScrollReady = false;
            _scrollCoordinator.OnTranscriptChanged();
        }
    }

    private bool TryPlaceInitialTranscript()
    {
        if (_isInitialLoading())
        {
            if (!_initialPlacementPending)
            {
                MarkInitialPlacementPending();
            }
            return true;
        }

        if (!_initialPlacementPending)
        {
            return false;
        }

        _changedBeforeScrollReady = false;
        if (!_hasTranscriptSelection() || !_hasRows())
        {
            CompleteInitialPlacement(_initialPlacementVersion);
            return true;
        }

        if (!_scrollViewer.IsVisible)
        {
            QueueVisibilityRetry();
            return true;
        }

        if (_initialPlacementQueued)
        {
            return true;
        }

        _initialPlacementQueued = true;
        var version = _initialPlacementVersion;
        _scrollViewer.Opacity = 0;
        _scrollCoordinator.QueueScrollToBottomAfterLayoutSettles(
            () => CompleteInitialPlacement(version),
            _initialPlacementCancellation?.Token ?? default);
        return true;
    }

    private void QueueVisibilityRetry()
    {
        if (_initialVisibilityRetryQueued)
        {
            return;
        }

        _initialVisibilityRetryQueued = true;
        _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (_disposed || cancellationToken.IsCancellationRequested)
                {
                    return;
                }

                _initialVisibilityRetryQueued = false;
                HandleTranscriptReady();
            }, DispatcherPriority.Loaded);
        });
    }

    private void CompleteInitialPlacement(int version)
    {
        if (_disposed || version != _initialPlacementVersion)
        {
            return;
        }

        _initialPlacementPending = false;
        _initialPlacementQueued = false;
        _initialVisibilityRetryQueued = false;
        _scrollViewer.Opacity = 1;
        _initialPlacementCancellation?.Dispose();
        _initialPlacementCancellation = null;
        _initialPresentation.TrySetResult();
    }

    private static TaskCompletionSource CreatePresentationCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
