using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LiveMarkdown.Avalonia;

namespace Sunder.Package.Agent.Shared.PackageViews;

internal sealed class StreamingMarkdownPresenter : Panel
{
    public static readonly DirectProperty<StreamingMarkdownPresenter, ObservableStringBuilder?> MarkdownBuilderProperty =
        AvaloniaProperty.RegisterDirect<StreamingMarkdownPresenter, ObservableStringBuilder?>(
            nameof(MarkdownBuilder),
            presenter => presenter.MarkdownBuilder,
            (presenter, value) => presenter.MarkdownBuilder = value);

    private ObservableStringBuilder? _markdownBuilder;
    private readonly List<Visual> _visibilitySources = [];
    private MarkdownRenderer? _currentRenderer;
    private MarkdownRenderer? _pendingRenderer;
    private SelectableTextBlock? _fallback;
    private string _requestedSource = string.Empty;
    private string _pendingSource = string.Empty;
    private string _renderedSource = string.Empty;
    private Task _refreshOperation = Task.CompletedTask;
    private Task _promotionOperation = Task.CompletedTask;
    private bool _isAttached;
    private bool _isSubscribed;
    private bool _refreshQueued;
    private bool _pendingReady;
    private bool _currentLayoutObserved;
    private Size _currentDesiredSize;

    public StreamingMarkdownPresenter()
    {
        ClipToBounds = false;
        MarkdownTextBlock.SetIsSelectionScope(this, true);
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
    }

    public event EventHandler? Rendered;

    internal Task PendingRenderOperations => Task.WhenAll(_refreshOperation, _promotionOperation);

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
            SetAndRaise(MarkdownBuilderProperty, ref _markdownBuilder, value);
            SubscribeToBuilder();
            ResetRenderedContent();
            RequestRefresh();
        }
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desiredSize = default(Size);
        foreach (var child in Children.ToArray())
        {
            child.Measure(availableSize);
            if (ReferenceEquals(child, _pendingRenderer))
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
        RequestRefresh();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs e)
    {
        _isAttached = false;
        ClearVisibilitySubscriptions();
        UnsubscribeFromBuilder();
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
        else
        {
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

        _requestedSource = _markdownBuilder?.ToString() ?? string.Empty;
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
            Dispatcher.UIThread.InvokeAsync(StartRequestedRender, DispatcherPriority.Render));
    }

    private void StartRequestedRender()
    {
        _refreshQueued = false;
        if (!CanRender || _pendingRenderer is not null || _requestedSource == _renderedSource)
        {
            return;
        }

        _pendingSource = _requestedSource;
        EnsureFallback();
        var renderer = new MarkdownRenderer
        {
            MarkdownBuilder = new ObservableStringBuilder(_pendingSource),
            MinWidth = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            Opacity = 0,
            IsHitTestVisible = false,
            IsEnabled = false,
            Focusable = false,
        };
        renderer.LayoutUpdated += OnPendingRendererLayoutUpdated;
        _pendingRenderer = renderer;
        _pendingReady = false;
        Children.Add(renderer);
    }

    private void OnPendingRendererLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not MarkdownRenderer renderer
            || !ReferenceEquals(renderer, _pendingRenderer)
            || !HasRenderedPendingContent(renderer))
        {
            return;
        }

        renderer.LayoutUpdated -= OnPendingRendererLayoutUpdated;
        _pendingReady = true;
        _promotionOperation = AwaitDispatcherOperationAsync(
            Dispatcher.UIThread.InvokeAsync(
                () => PromotePendingRenderer(renderer),
                DispatcherPriority.Background));
    }

    private bool HasRenderedPendingContent(MarkdownRenderer renderer)
        => string.IsNullOrWhiteSpace(_pendingSource)
           || renderer.GetVisualDescendants().Skip(1).Any();

    private void PromotePendingRenderer(MarkdownRenderer renderer)
    {
        if (!CanRender || !ReferenceEquals(renderer, _pendingRenderer))
        {
            return;
        }
        if (!string.Equals(_pendingSource, _requestedSource, StringComparison.Ordinal))
        {
            DiscardPendingRenderer(renderer);
            RequestRefresh();
            return;
        }
        if (_currentRenderer is { CanCopy: true }
            || _currentRenderer?.IsKeyboardFocusWithin == true
            || _fallback is { } fallback
            && (!string.IsNullOrEmpty(fallback.SelectedText)
                || fallback.IsKeyboardFocusWithin))
        {
            return;
        }

        if (_currentRenderer is not null)
        {
            _currentRenderer.PropertyChanged -= OnCurrentRendererPropertyChanged;
            _currentRenderer.LayoutUpdated -= OnCurrentRendererLayoutUpdated;
            _currentRenderer.MarkdownBuilder = null;
            Children.Remove(_currentRenderer);
        }
        if (_fallback is not null)
        {
            _fallback.PropertyChanged -= OnFallbackPropertyChanged;
            Children.Remove(_fallback);
            _fallback = null;
        }

        _pendingRenderer = null;
        _pendingReady = false;
        _currentRenderer = renderer;
        renderer.PropertyChanged += OnCurrentRendererPropertyChanged;
        renderer.LayoutUpdated += OnCurrentRendererLayoutUpdated;
        _currentLayoutObserved = false;
        _currentDesiredSize = renderer.DesiredSize;
        _renderedSource = _pendingSource;
        renderer.Opacity = 1;
        renderer.IsHitTestVisible = true;
        renderer.IsEnabled = true;
        renderer.Focusable = true;
        Rendered?.Invoke(this, EventArgs.Empty);
        if (_requestedSource != _renderedSource)
        {
            RequestRefresh();
        }
    }

    private void OnCurrentRendererLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not MarkdownRenderer renderer
            || !ReferenceEquals(renderer, _currentRenderer))
        {
            return;
        }

        var desiredSize = renderer.DesiredSize;
        if (!_currentLayoutObserved)
        {
            _currentLayoutObserved = true;
            _currentDesiredSize = desiredSize;
            return;
        }
        if (Math.Abs(desiredSize.Width - _currentDesiredSize.Width) < 0.5
            && Math.Abs(desiredSize.Height - _currentDesiredSize.Height) < 0.5)
        {
            return;
        }

        _currentDesiredSize = desiredSize;
        Rendered?.Invoke(this, EventArgs.Empty);
    }

    private void EnsureFallback()
    {
        if (_currentRenderer is not null || _fallback is not null)
        {
            return;
        }

        _fallback = new SelectableTextBlock
        {
            Text = _pendingSource,
            TextWrapping = TextWrapping.Wrap,
        };
        _fallback.Classes.Add("markdown-fallback");
        _fallback.PropertyChanged += OnFallbackPropertyChanged;
        Children.Add(_fallback);
    }

    private void DiscardPendingRenderer(MarkdownRenderer renderer)
    {
        renderer.LayoutUpdated -= OnPendingRendererLayoutUpdated;
        renderer.MarkdownBuilder = null;
        Children.Remove(renderer);
        _pendingRenderer = null;
        _pendingReady = false;
        _pendingSource = string.Empty;
    }

    private void OnCurrentRendererPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is MarkdownRenderer renderer
            && ReferenceEquals(renderer, _currentRenderer)
            && _pendingRenderer is { } pendingRenderer
            && _pendingReady
            && !renderer.CanCopy
            && !renderer.IsKeyboardFocusWithin)
        {
            PromotePendingRenderer(pendingRenderer);
        }
    }

    private void OnFallbackPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (sender is SelectableTextBlock fallback
            && ReferenceEquals(fallback, _fallback)
            && _pendingRenderer is { } pendingRenderer
            && _pendingReady
            && string.IsNullOrEmpty(fallback.SelectedText)
            && !fallback.IsKeyboardFocusWithin)
        {
            PromotePendingRenderer(pendingRenderer);
        }
    }

    private void ResetRenderedContent()
    {
        if (_pendingRenderer is not null)
        {
            _pendingRenderer.LayoutUpdated -= OnPendingRendererLayoutUpdated;
            _pendingRenderer.MarkdownBuilder = null;
        }
        if (_currentRenderer is not null)
        {
            _currentRenderer.PropertyChanged -= OnCurrentRendererPropertyChanged;
            _currentRenderer.LayoutUpdated -= OnCurrentRendererLayoutUpdated;
            _currentRenderer.MarkdownBuilder = null;
        }
        if (_fallback is not null)
        {
            _fallback.PropertyChanged -= OnFallbackPropertyChanged;
        }

        Children.Clear();
        _pendingRenderer = null;
        _pendingReady = false;
        _currentRenderer = null;
        _currentLayoutObserved = false;
        _currentDesiredSize = default;
        _fallback = null;
        _pendingSource = string.Empty;
        _renderedSource = string.Empty;
    }

    private static async Task AwaitDispatcherOperationAsync(DispatcherOperation operation)
        => await operation;
}
