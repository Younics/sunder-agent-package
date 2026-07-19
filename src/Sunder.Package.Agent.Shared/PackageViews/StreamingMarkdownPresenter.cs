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
    private StableMarkdownRenderer? _currentRenderer;
    private ObservableStringBuilder? _displayBuilder;
    private SelectableTextBlock? _fallback;
    private string _requestedSource = string.Empty;
    private string _appliedSource = string.Empty;
    private string _notifiedRenderedSource = string.Empty;
    private Task _refreshOperation = Task.CompletedTask;
    private bool _isAttached;
    private bool _isSubscribed;
    private bool _refreshQueued;
    private bool _hasRenderedContent;
    private bool _initialLayoutNotificationPending;
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

    internal Task PendingRenderOperations => Task.WhenAll(
        _refreshOperation,
        _currentRenderer?.PendingRenderOperations ?? Task.CompletedTask);

    internal bool IsRenderPending
        => _refreshQueued
           || !_refreshOperation.IsCompleted
           || _currentRenderer is null
           || !_currentRenderer.PendingRenderOperations.IsCompleted
           || !string.Equals(_requestedSource, _appliedSource, StringComparison.Ordinal)
           || !HasCurrentTerminalRenderFailure
           && (!string.Equals(_currentRenderer.RenderedSource, _appliedSource, StringComparison.Ordinal)
               || !_hasRenderedContent
               || _initialLayoutNotificationPending
               || !_currentLayoutObserved);

    internal int RendererCreationCount { get; private set; }

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
            SetAndRaise(MarkdownBuilderProperty, ref _markdownBuilder, value);
            SubscribeToBuilder();
            ResetRenderedContent();
            _requestedSource = value?.ToString() ?? string.Empty;
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
        else if (!IsVisible)
        {
            // A role branch that is itself hidden should not retain duplicate Markdown visuals.
            // Ancestor visibility changes are temporary package-view suspension and retain state.
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
        var renderer = StableMarkdownRenderer.Create();
        renderer.SourceBuilder = _displayBuilder;
        renderer.MinWidth = 0;
        renderer.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch;
        renderer.Opacity = 0;
        renderer.IsHitTestVisible = false;
        renderer.IsEnabled = false;
        renderer.Focusable = false;
        _currentRenderer = renderer;
        RendererCreationCount++;
        renderer.PropertyChanged += OnCurrentRendererPropertyChanged;
        renderer.LayoutUpdated += OnCurrentRendererLayoutUpdated;
        Children.Add(renderer);
    }

    private void OnCurrentRendererLayoutUpdated(object? sender, EventArgs e)
    {
        if (sender is not StableMarkdownRenderer renderer
            || !ReferenceEquals(renderer, _currentRenderer))
        {
            return;
        }

        var desiredSize = renderer.DesiredSize;
        if (!_hasRenderedContent)
        {
            TryPromoteInitialRenderer();
            return;
        }
        if (_initialLayoutNotificationPending)
        {
            if (!_currentLayoutObserved)
            {
                _currentLayoutObserved = true;
                _currentDesiredSize = desiredSize;
                return;
            }
            if (Math.Abs(desiredSize.Height - _currentDesiredSize.Height) >= 0.5)
            {
                _currentDesiredSize = desiredSize;
                return;
            }

            _initialLayoutNotificationPending = false;
            _currentDesiredSize = desiredSize;
            _notifiedRenderedSource = renderer.RenderedSource;
            Rendered?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (!_currentLayoutObserved)
        {
            _currentLayoutObserved = true;
            _currentDesiredSize = desiredSize;
            return;
        }
        if (Math.Abs(desiredSize.Height - _currentDesiredSize.Height) < 0.5)
        {
            _currentDesiredSize = desiredSize;
            if (!string.Equals(
                    _notifiedRenderedSource,
                    renderer.RenderedSource,
                    StringComparison.Ordinal))
            {
                _notifiedRenderedSource = renderer.RenderedSource;
                Rendered?.Invoke(this, EventArgs.Empty);
            }
            return;
        }

        _currentDesiredSize = desiredSize;
        _notifiedRenderedSource = renderer.RenderedSource;
        Rendered?.Invoke(this, EventArgs.Empty);
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
        _initialLayoutNotificationPending = true;
        _currentLayoutObserved = false;
        InvalidateMeasure();
    }

    private void EnsureFallback()
    {
        if (_currentRenderer is not null || _fallback is not null)
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

    private void ResetRenderedContent()
    {
        if (_currentRenderer is not null)
        {
            _currentRenderer.PropertyChanged -= OnCurrentRendererPropertyChanged;
            _currentRenderer.LayoutUpdated -= OnCurrentRendererLayoutUpdated;
            _currentRenderer.SourceBuilder = null;
        }
        if (_fallback is not null)
        {
            _fallback.PropertyChanged -= OnFallbackPropertyChanged;
        }

        Children.Clear();
        _currentRenderer = null;
        _displayBuilder = null;
        _hasRenderedContent = false;
        _initialLayoutNotificationPending = false;
        _currentLayoutObserved = false;
        _currentDesiredSize = default;
        _notifiedRenderedSource = string.Empty;
        _fallback = null;
        _appliedSource = string.Empty;
    }

    private static async Task AwaitDispatcherOperationAsync(DispatcherOperation operation)
        => await operation;
}
