using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class StreamingMarkdownPresenter : Panel, ITranscriptGeometrySource
{
    internal static readonly AttachedProperty<bool> IsPreparingToolDetailProperty =
        AvaloniaProperty.RegisterAttached<StreamingMarkdownPresenter, StyledElement, bool>(
            "IsPreparingToolDetail",
            inherits: true);

    public static readonly DirectProperty<StreamingMarkdownPresenter, ObservableStringBuilder?> MarkdownBuilderProperty =
        AvaloniaProperty.RegisterDirect<StreamingMarkdownPresenter, ObservableStringBuilder?>(
            nameof(MarkdownBuilder),
            presenter => presenter.MarkdownBuilder,
            (presenter, value) => presenter.MarkdownBuilder = value);

    private ObservableStringBuilder? _markdownBuilder;
    private readonly Func<StableMarkdownRenderer> _createRenderer;
    private readonly List<Visual> _visibilitySources = [];
    private StableMarkdownRenderer? _currentRenderer;
    private ObservableStringBuilder? _displayBuilder;
    private SelectableTextBlock? _fallback;
    private string _requestedSource = string.Empty;
    private string _appliedSource = string.Empty;
    private Task _refreshOperation = Task.CompletedTask;
    private Task _geometrySettlementOperation = Task.CompletedTask;
    private bool _isAttached;
    private bool _isSubscribed;
    private bool _refreshQueued;
    private bool _hasRenderedContent;
    private bool _currentLayoutObserved;
    private bool _geometryConfirmationQueued;
    private int _stableLayoutObservations;
    private Size _currentDesiredSize;
    private long _appliedRevision;

    public StreamingMarkdownPresenter()
        : this(() => StableMarkdownRenderer.Create())
    {
    }

    internal StreamingMarkdownPresenter(Func<StableMarkdownRenderer> createRenderer)
    {
        _createRenderer = createRenderer;
        ClipToBounds = false;
        MarkdownTextBlock.SetIsSelectionScope(this, true);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        LayoutUpdated += OnPresenterLayoutUpdated;
    }

    public event EventHandler? Rendered;

    public event EventHandler? GeometryChanged;

    public long RequestedRevision { get; private set; }

    public long SettledRevision { get; private set; }

    public long GeometryRevision { get; private set; }

    public bool IsGeometryPending => SettledRevision < RequestedRevision;

    internal Task PendingRenderOperations => Task.WhenAll(
        _refreshOperation,
        _geometrySettlementOperation,
        _currentRenderer?.PendingRenderOperations ?? Task.CompletedTask);

    internal bool IsRenderPending
        => _refreshQueued
           || !_refreshOperation.IsCompleted
           || IsGeometryPending
           || _currentRenderer is null
           || !_currentRenderer.PendingRenderOperations.IsCompleted
           || !string.Equals(_requestedSource, _appliedSource, StringComparison.Ordinal)
           || !HasCurrentTerminalRenderFailure
           && (!string.Equals(_currentRenderer.RenderedSource, _appliedSource, StringComparison.Ordinal)
                || !_hasRenderedContent
                || !_currentLayoutObserved);

    internal int RendererCreationCount { get; private set; }

    internal bool PreserveRenderedContentOnDetach { get; set; }

    internal static void SetIsPreparingToolDetail(StyledElement element, bool value)
        => element.SetValue(IsPreparingToolDetailProperty, value);

    private bool HasCurrentTerminalRenderFailure
        => _currentRenderer?.HasTerminalRenderFailure == true
           && string.Equals(_requestedSource, _appliedSource, StringComparison.Ordinal);

    public ObservableStringBuilder? MarkdownBuilder
    {
        get => _markdownBuilder;
        set
        {
            if (ReferenceEquals(_markdownBuilder, value))
            {
                return;
            }

            UnsubscribeFromBuilder();
            SettleOutstandingGeometry();
            SetAndRaise(MarkdownBuilderProperty, ref _markdownBuilder, value);
            SubscribeToBuilder();
            ResetRenderedContent();
            CaptureRequestedSource(forceRevision: true);
            RequestRefresh();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desiredSize = default(Size);
        foreach (var child in Children.ToArray())
        {
            child.Measure(availableSize);
            if (ReferenceEquals(child, _currentRenderer) && !_hasRenderedContent)
            {
                continue;
            }
            desiredSize = new Size(
                Math.Max(desiredSize.Width, child.DesiredSize.Width),
                Math.Max(desiredSize.Height, child.DesiredSize.Height));
        }

        return desiredSize;
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children.ToArray())
        {
            child.Arrange(new Rect(finalSize));
        }

        return finalSize;
    }

    private bool CanRender => _isAttached && IsEffectivelyVisible;

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = true;
        RefreshVisibilitySubscriptions();
        SubscribeToBuilder();
        if (SettledRevision == RequestedRevision && _currentRenderer is null)
        {
            CaptureRequestedSource(forceRevision: true);
        }
        RequestRefresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        ClearVisibilitySubscriptions();
        UnsubscribeFromBuilder();
        if (PreserveRenderedContentOnDetach)
        {
            ResetLayoutSettlement();
            return;
        }

        SettleOutstandingGeometry();
        ResetRenderedContent();
    }

    private void OnVisibilitySourcePropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty)
        {
            return;
        }

        if (CanRender)
        {
            SubscribeToBuilder();
            RequestRefresh();
        }
        else if (!IsVisible)
        {
            // A role branch that is itself hidden should not retain duplicate Markdown visuals.
            // Ancestor visibility changes are temporary package-view suspension and retain state.
            SettleOutstandingGeometry();
            ResetRenderedContent();
        }
    }

    private void RefreshVisibilitySubscriptions()
    {
        ClearVisibilitySubscriptions();
        _visibilitySources.Add(this);
        _visibilitySources.AddRange(this.GetVisualAncestors());
        foreach (var source in _visibilitySources)
        {
            source.PropertyChanged += OnVisibilitySourcePropertyChanged;
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

    private void SubscribeToBuilder()
    {
        if (_isSubscribed || !CanRender || _markdownBuilder is null)
        {
            return;
        }

        _markdownBuilder.Changed += OnMarkdownChanged;
        _isSubscribed = true;
    }

    private void UnsubscribeFromBuilder()
    {
        if (!_isSubscribed || _markdownBuilder is null)
        {
            _isSubscribed = false;
            return;
        }

        _markdownBuilder.Changed -= OnMarkdownChanged;
        _isSubscribed = false;
    }

    private void OnMarkdownChanged(in ObservableStringBuilderChangedEventArgs e)
        => RequestRefresh();

    private void RequestRefresh()
    {
        if (!CanRender)
        {
            return;
        }

        CaptureRequestedSource();
        EnsureFallback();
        if (_fallback is { } fallback
            && string.IsNullOrEmpty(fallback.SelectedText)
            && !fallback.IsKeyboardFocusWithin)
        {
            fallback.Text = _requestedSource;
        }

        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;
        _refreshOperation = AwaitDispatcherOperationAsync(
            Dispatcher.UIThread.InvokeAsync(ApplyRequestedSource, DispatcherPriority.Render));
    }

    private void ApplyRequestedSource()
    {
        _refreshQueued = false;
        if (!CanRender)
        {
            return;
        }

        _requestedSource = _markdownBuilder?.ToString() ?? string.Empty;
        CaptureRequestedSource();
        EnsureRenderer();
        if (_currentRenderer is null || _displayBuilder is null)
        {
            return;
        }
        if (_currentRenderer.CanCopy
            || _currentRenderer.IsKeyboardFocusWithin
            || _fallback is { } selectedFallback
            && (!string.IsNullOrEmpty(selectedFallback.SelectedText)
                || selectedFallback.IsKeyboardFocusWithin))
        {
            return;
        }
        if (string.Equals(_requestedSource, _appliedSource, StringComparison.Ordinal))
        {
            _appliedRevision = RequestedRevision;
            TryPromoteInitialRenderer();
            return;
        }

        if (_requestedSource.StartsWith(_appliedSource, StringComparison.Ordinal))
        {
            _displayBuilder.Append(_requestedSource[_appliedSource.Length..]);
        }
        else
        {
            _displayBuilder.Clear();
            _displayBuilder.Append(_requestedSource);
        }

        _appliedSource = _requestedSource;
        _appliedRevision = RequestedRevision;
        ResetLayoutSettlement();
        if (_fallback is { } fallback
            && string.IsNullOrEmpty(fallback.SelectedText)
            && !fallback.IsKeyboardFocusWithin)
        {
            fallback.Text = _appliedSource;
        }
        TryPromoteInitialRenderer();
    }

    private void EnsureRenderer()
    {
        if (_currentRenderer is not null)
        {
            return;
        }

        EnsureFallback();
        _displayBuilder = new ObservableStringBuilder();
        var renderer = _createRenderer();
        renderer.SourceBuilder = _displayBuilder;
        renderer.MinWidth = 0;
        renderer.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        renderer.Opacity = 0;
        renderer.IsHitTestVisible = false;
        renderer.IsEnabled = false;
        renderer.Focusable = false;
        _currentRenderer = renderer;
        RendererCreationCount++;
        TranscriptToolDiagnostics.MarkdownRendererCreated();
        renderer.PropertyChanged += OnCurrentRendererPropertyChanged;
        renderer.RenderStateChanged += OnCurrentRendererRenderStateChanged;
        Children.Add(renderer);
    }

    private void OnPresenterLayoutUpdated(object? sender, EventArgs e)
    {
        TryPromoteInitialRenderer();
        var desiredSize = DesiredSize;
        if (!_currentLayoutObserved)
        {
            _currentLayoutObserved = true;
            _currentDesiredSize = desiredSize;
            _stableLayoutObservations = 0;
            PublishGeometryChange();
        }
        else if (GeometryChangedByAtLeastHalfPixel(desiredSize, _currentDesiredSize))
        {
            _currentDesiredSize = desiredSize;
            _stableLayoutObservations = 0;
            PublishGeometryChange(raiseRenderedWhenSettled: true);
        }
        else
        {
            _stableLayoutObservations++;
        }

        if (!CanSettleCurrentRevision())
        {
            return;
        }

        if (_stableLayoutObservations >= 2)
        {
            SettleCurrentRevision();
        }
        else
        {
            QueueGeometryConfirmation();
        }
    }

    private void OnCurrentRendererRenderStateChanged(object? sender, EventArgs e)
    {
        if (sender is not StableMarkdownRenderer renderer
            || !ReferenceEquals(renderer, _currentRenderer))
        {
            return;
        }

        if (HasCurrentTerminalRenderFailure)
        {
            SettleCurrentRevision();
            return;
        }

        TryPromoteInitialRenderer();
        if (HasRenderedCurrentContent(renderer))
        {
            ResetLayoutSettlement();
            InvalidateMeasure();
            QueueGeometryConfirmation();
        }
    }

    private bool HasRenderedCurrentContent(StableMarkdownRenderer renderer)
        => string.Equals(renderer.RenderedSource, _appliedSource, StringComparison.Ordinal);

    private void TryPromoteInitialRenderer()
    {
        if (_hasRenderedContent
            || _currentRenderer is not { } renderer
            || !HasRenderedCurrentContent(renderer)
            || _fallback is { } fallback
            && (!string.IsNullOrEmpty(fallback.SelectedText)
                || fallback.IsKeyboardFocusWithin))
        {
            return;
        }

        _hasRenderedContent = true;
        if (_fallback is not null)
        {
            _fallback.PropertyChanged -= OnFallbackPropertyChanged;
            Children.Remove(_fallback);
            _fallback = null;
        }
        renderer.Opacity = 1;
        renderer.IsHitTestVisible = true;
        renderer.IsEnabled = true;
        renderer.Focusable = true;
        ResetLayoutSettlement();
        PublishGeometryChange();
        InvalidateMeasure();
        QueueGeometryConfirmation();
    }

    private void EnsureFallback()
    {
        if (_currentRenderer is not null
            || _fallback is not null
            || GetValue(IsPreparingToolDetailProperty))
        {
            return;
        }

        _fallback = new SelectableTextBlock
        {
            Text = _requestedSource,
            TextWrapping = TextWrapping.Wrap,
        };
        _fallback.Classes.Add("markdown-fallback");
        _fallback.PropertyChanged += OnFallbackPropertyChanged;
        Children.Add(_fallback);
    }

    private void OnCurrentRendererPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is StableMarkdownRenderer renderer
            && ReferenceEquals(renderer, _currentRenderer)
            && !renderer.CanCopy
            && !renderer.IsKeyboardFocusWithin)
        {
            renderer.ResumeDeferredRender();
            if (!string.Equals(_requestedSource, _appliedSource, StringComparison.Ordinal))
            {
                RequestRefresh();
            }
        }
    }

    private void OnFallbackPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is SelectableTextBlock fallback
            && ReferenceEquals(fallback, _fallback)
            && string.IsNullOrEmpty(fallback.SelectedText)
            && !fallback.IsKeyboardFocusWithin)
        {
            if (string.Equals(_requestedSource, _appliedSource, StringComparison.Ordinal))
            {
                TryPromoteInitialRenderer();
            }
            else
            {
                RequestRefresh();
            }
        }
    }

    private void CaptureRequestedSource(bool forceRevision = false)
    {
        var source = _markdownBuilder?.ToString() ?? string.Empty;
        if (!forceRevision && string.Equals(source, _requestedSource, StringComparison.Ordinal))
        {
            return;
        }

        _requestedSource = source;
        RequestedRevision++;
        ResetLayoutSettlement();
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    private bool CanSettleCurrentRevision()
        => IsGeometryPending
           && _appliedRevision == RequestedRevision
           && (HasCurrentTerminalRenderFailure
               || _hasRenderedContent
               && _currentRenderer is { } renderer
               && HasRenderedCurrentContent(renderer));

    private void SettleCurrentRevision()
    {
        if (!IsGeometryPending)
        {
            return;
        }

        SettledRevision = RequestedRevision;
        GeometryRevision++;
        GeometryChanged?.Invoke(this, EventArgs.Empty);
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    private void SettleOutstandingGeometry()
    {
        if (!IsGeometryPending)
        {
            return;
        }

        SettledRevision = RequestedRevision;
        GeometryRevision++;
        GeometryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void PublishGeometryChange(bool raiseRenderedWhenSettled = false)
    {
        GeometryRevision++;
        GeometryChanged?.Invoke(this, EventArgs.Empty);
        if (raiseRenderedWhenSettled
            && !IsGeometryPending
            && _currentRenderer is { } renderer
            && HasRenderedCurrentContent(renderer))
        {
            Rendered?.Invoke(this, EventArgs.Empty);
        }
    }

    private void ResetLayoutSettlement()
    {
        _currentLayoutObserved = false;
        _stableLayoutObservations = 0;
        _currentDesiredSize = default;
    }

    private void QueueGeometryConfirmation()
    {
        if (_geometryConfirmationQueued || !CanRender || !IsGeometryPending)
        {
            return;
        }

        _geometryConfirmationQueued = true;
        _geometrySettlementOperation = AwaitDispatcherOperationAsync(
            Dispatcher.UIThread.InvokeAsync(
                () =>
                {
                    _geometryConfirmationQueued = false;
                    if (!CanRender || !IsGeometryPending)
                    {
                        return;
                    }

                    _currentRenderer?.InvalidateMeasure();
                    InvalidateMeasure();
                },
                DispatcherPriority.Background));
    }

    private static bool GeometryChangedByAtLeastHalfPixel(Size current, Size previous)
        => Math.Abs(current.Width - previous.Width) >= 0.5
           || Math.Abs(current.Height - previous.Height) >= 0.5;

    private void ResetRenderedContent()
    {
        if (_currentRenderer is not null)
        {
            _currentRenderer.PropertyChanged -= OnCurrentRendererPropertyChanged;
            _currentRenderer.RenderStateChanged -= OnCurrentRendererRenderStateChanged;
            _currentRenderer.SourceBuilder = null;
            TranscriptToolDiagnostics.MarkdownRendererDestroyed();
        }
        if (_fallback is not null)
        {
            _fallback.PropertyChanged -= OnFallbackPropertyChanged;
        }

        Children.Clear();
        _currentRenderer = null;
        _displayBuilder = null;
        _hasRenderedContent = false;
        _currentLayoutObserved = false;
        _stableLayoutObservations = 0;
        _currentDesiredSize = default;
        _fallback = null;
        _appliedSource = string.Empty;
        _appliedRevision = 0;
    }

    private static async Task AwaitDispatcherOperationAsync(DispatcherOperation operation)
        => await operation;
}
