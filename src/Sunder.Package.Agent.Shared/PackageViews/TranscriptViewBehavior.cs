using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private readonly Func<bool> _isFollowingLatest;
    private readonly Func<TranscriptViewportAnchorData?>? _getViewportAnchor;
    private readonly Action<bool>? _presentationStateChanged;
    private readonly TranscriptScrollCoordinator _scrollCoordinator;
    private readonly List<Visual> _visibilitySources = [];
    private bool _changedBeforeScrollReady;
    private bool _restoreAnchorOnActivation;
    private bool _initialPlacementPending = true;
    private bool _initialPlacementQueued;
    private int _initialPlacementVersion;
    private TaskCompletionSource _initialPresentation = CreatePresentationCompletion();
    private CancellationTokenSource? _initialPlacementCancellation;
    private CancellationTokenRegistration? _initialPlacementCancellationRegistration;
    private Task _initialPlacementCancellationOperation = Task.CompletedTask;
    private bool _loaded;
    private bool _presentationActive = true;
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
        Func<bool> isFollowingLatest,
        Func<bool> isInitialLoading,
        Func<bool> hasRows,
        Func<bool> hasTranscriptSelection,
        Func<bool> detachFromLatest,
        Func<bool> reachedLatest,
        Action<bool>? jumpVisibilityChanged = null,
        Action<TranscriptViewportAnchorData?>? viewportAnchorChanged = null,
        Func<TranscriptViewportAnchorData?>? getViewportAnchor = null,
        Action<Exception>? pagingFailed = null,
        Func<IEnumerable<(object Item, Control Visual)>>? enumerateRealizedAnchors = null,
        Func<object, Control?>? realizeAnchorVisual = null,
        Action<bool>? presentationStateChanged = null,
        TranscriptScrollAnchorHost? anchorHost = null)
    {
        _owner = owner;
        _scrollViewer = scrollViewer;
        _itemsControl = itemsControl;
        _jumpToLatestButton = jumpToLatestButton;
        _isInitialLoading = isInitialLoading;
        _hasRows = hasRows;
        _hasTranscriptSelection = hasTranscriptSelection;
        _isFollowingLatest = isFollowingLatest;
        _getViewportAnchor = getViewportAnchor;
        _presentationStateChanged = presentationStateChanged;
        _scrollViewer.Opacity = 0;
        _scrollCoordinator = new TranscriptScrollCoordinator(
            scrollViewer,
            itemsControl,
            canLoadOlder,
            loadOlder,
            canLoadNewer,
            loadNewer,
            hasNewer,
            isFollowingLatest,
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
            realizeAnchorVisual,
            anchorHost: anchorHost);
        _owner.Loaded += OnLoaded;
        _owner.AttachedToVisualTree += OnPresentationStateChanged;
        _owner.DetachedFromVisualTree += OnPresentationStateChanged;
        _scrollViewer.AttachedToVisualTree += OnPresentationStateChanged;
        _scrollViewer.DetachedFromVisualTree += OnPresentationStateChanged;
        RefreshVisibilitySubscriptions();
    }

    public void OnTranscriptChanging(bool isPaging)
    {
        if (_disposed || _initialPlacementPending)
        {
            return;
        }

        if (!_loaded || !_presentationActive)
        {
            _changedBeforeScrollReady = true;
            return;
        }

        if (_isInitialLoading())
        {
            _scrollCoordinator.BeginTranscriptReplacementMutation();
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

        _initialPlacementVersion++;

        _initialPlacementCancellationRegistration?.Dispose();
        _initialPlacementCancellationRegistration = null;
        _initialPlacementCancellation?.Cancel();
        _initialPlacementCancellation?.Dispose();
        _initialPlacementCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        if (_initialPresentation.Task.IsCompleted || _initialPlacementQueued)
        {
            _initialPresentation = CreatePresentationCompletion();
        }

        _initialPlacementPending = true;
        _initialPlacementQueued = false;
        _scrollViewer.Opacity = 0;
        _scrollCoordinator.BeginInitialPlacement();
        var version = _initialPlacementVersion;
        var placementCancellationToken = _initialPlacementCancellation.Token;
        _initialPlacementCancellationRegistration = placementCancellationToken.Register(() =>
        {
            _initialPlacementCancellationOperation = AwaitDispatcherOperationAsync(
                Dispatcher.UIThread.InvokeAsync(
                    () => CancelInitialPlacement(version, placementCancellationToken),
                    DispatcherPriority.Background));
        });
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

    public void FollowLatestFromExplicitIntent()
        => _scrollCoordinator.QueueScrollToBottom(force: true);

    public void OnRenderedContentChanged()
    {
        if (!_disposed && _loaded && _presentationActive && !_initialPlacementPending)
        {
            _scrollCoordinator.OnRenderedContentChanged();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _initialPlacementCancellationRegistration?.Dispose();
        _initialPlacementCancellationRegistration = null;
        _initialPlacementCancellation?.Cancel();
        _initialPlacementCancellation?.Dispose();
        _initialPlacementCancellation = null;
        _initialPresentation.TrySetCanceled();
        _owner.Loaded -= OnLoaded;
        _owner.AttachedToVisualTree -= OnPresentationStateChanged;
        _owner.DetachedFromVisualTree -= OnPresentationStateChanged;
        _scrollViewer.AttachedToVisualTree -= OnPresentationStateChanged;
        _scrollViewer.DetachedFromVisualTree -= OnPresentationStateChanged;
        ClearVisibilitySubscriptions();
        _presentationStateChanged?.Invoke(false);
        _scrollCoordinator.Dispose();
    }

    private void OnLoaded(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
        => RefreshPresentationState();

    private void OnPresentationStateChanged(object? sender, EventArgs e)
    {
        RefreshVisibilitySubscriptions();
        RefreshPresentationState();
    }

    private void OnVisibilitySourcePropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty)
        {
            RefreshPresentationState();
        }
    }

    private void RefreshVisibilitySubscriptions()
    {
        ClearVisibilitySubscriptions();
        var sources = new HashSet<Visual>();
        sources.Add(_owner);
        sources.UnionWith(_owner.GetVisualAncestors());
        sources.Add(_scrollViewer);
        sources.UnionWith(_scrollViewer.GetVisualAncestors());
        foreach (var source in sources)
        {
            source.PropertyChanged += OnVisibilitySourcePropertyChanged;
            _visibilitySources.Add(source);
        }
    }

    private void ClearVisibilitySubscriptions()
    {
        foreach (var source in _visibilitySources)
        {
            source.PropertyChanged -= OnVisibilitySourcePropertyChanged;
        }
        _visibilitySources.Clear();
    }

    private void RefreshPresentationState()
    {
        var isOwnerActive = _owner.IsAttachedToVisualTree() && _owner.IsEffectivelyVisible;
        var isPresentationActive = isOwnerActive
                                   && _scrollViewer.IsAttachedToVisualTree()
                                   && _scrollViewer.IsEffectivelyVisible;
        var ownerChanged = _loaded != isOwnerActive;
        var presentationChanged = _presentationActive != isPresentationActive;
        if (!ownerChanged && !presentationChanged)
        {
            return;
        }

        _loaded = isOwnerActive;
        if (presentationChanged)
        {
            if (!isPresentationActive && !_isFollowingLatest())
            {
                _restoreAnchorOnActivation = true;
            }
            _presentationActive = isPresentationActive;
            _scrollCoordinator.SetPresentationActive(isPresentationActive);
            if (!isPresentationActive && _initialPlacementPending)
            {
                _initialPlacementQueued = false;
                _changedBeforeScrollReady = true;
            }
            _presentationStateChanged?.Invoke(isPresentationActive);
            if (isPresentationActive
                && (_changedBeforeScrollReady || _restoreAnchorOnActivation)
                && !_isFollowingLatest())
            {
                var viewportAnchor = _getViewportAnchor?.Invoke();
                _scrollCoordinator.RestoreViewportAnchor(viewportAnchor);
                if (viewportAnchor is null)
                {
                    _scrollCoordinator.ReevaluatePagingEdges();
                }
            }
            if (isPresentationActive)
            {
                _restoreAnchorOnActivation = false;
            }
        }

        if (isOwnerActive)
        {
            HandleTranscriptReady();
        }
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
            if (!_isFollowingLatest())
            {
                _changedBeforeScrollReady = true;
                return true;
            }

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
        if (!_presentationActive)
        {
            _changedBeforeScrollReady = true;
            return true;
        }

        if (_initialPlacementQueued)
        {
            return true;
        }

        _initialPlacementQueued = true;
        var version = _initialPlacementVersion;
        var placementCancellationToken = _initialPlacementCancellation?.Token ?? default;
        _scrollViewer.Opacity = 0;
        _scrollCoordinator.QueueScrollToBottomAfterLayoutSettles(
            () => CompleteInitialPlacement(version, placementCancellationToken),
            placementCancellationToken);
        return true;
    }

    private void CompleteInitialPlacement(
        int version,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || version != _initialPlacementVersion)
        {
            return;
        }

        _initialPlacementPending = false;
        _initialPlacementQueued = false;
        _scrollViewer.Opacity = 1;
        _initialPlacementCancellation?.Dispose();
        _initialPlacementCancellation = null;
        _initialPlacementCancellationRegistration?.Dispose();
        _initialPlacementCancellationRegistration = null;
        if (cancellationToken.IsCancellationRequested)
        {
            _initialPresentation.TrySetCanceled(cancellationToken);
        }
        else
        {
            _initialPresentation.TrySetResult();
        }
    }

    private void CancelInitialPlacement(int version, CancellationToken cancellationToken)
    {
        if (_disposed || version != _initialPlacementVersion || !_initialPlacementPending)
        {
            return;
        }

        _initialPlacementVersion++;
        _initialPlacementPending = false;
        _initialPlacementQueued = false;
        _scrollViewer.Opacity = 1;
        _initialPlacementCancellationRegistration?.Dispose();
        _initialPlacementCancellationRegistration = null;
        _initialPlacementCancellation?.Dispose();
        _initialPlacementCancellation = null;
        _initialPresentation.TrySetCanceled(cancellationToken);
    }

    private static async Task AwaitDispatcherOperationAsync(DispatcherOperation operation)
        => await operation;

    private static TaskCompletionSource CreatePresentationCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);
}
