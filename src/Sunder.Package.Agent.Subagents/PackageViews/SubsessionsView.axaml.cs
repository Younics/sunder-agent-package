using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubsessionsView : UserControl
{
    private const double AutoScrollThreshold = 24;
    private const double LoadOlderThreshold = 36;
    private const double WideSubsessionMinimumWidth = 820;

    private SubsessionsViewModel? _viewModel;
    private bool _shouldAutoScroll = true;
    private bool _isProgrammaticScroll;
    private bool _scrollToBottomPending;
    private bool _loadOlderPending;

    public SubsessionsView()
    {
        InitializeComponent();
        TranscriptScrollViewer.PropertyChanged += OnScrollViewerPropertyChanged;
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout();
            QueueScrollToBottom();
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public SubsessionsView(SubsessionsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        DataContext = viewModel;
        _ = viewModel.InitializeAsync();
    }

    private void ApplyResponsiveLayout()
    {
        var useCompactLayout = Bounds.Width > 0 && Bounds.Width < WideSubsessionMinimumWidth;
        var viewModel = _viewModel ?? DataContext as SubsessionsViewModel;
        if (viewModel is not null)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }

        SubsessionAdaptiveLayout.ColumnSpacing = useCompactLayout ? 0 : 4;
        SubsessionListPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(SubsessionListPane, 0);
        Grid.SetColumn(SubsessionDetailPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(SubsessionListPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(SubsessionDetailPane, useCompactLayout ? 2 : 1);
    }

    private void OnSubsessionItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SubsessionListItemViewModel subsession)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as SubsessionsViewModel;
        viewModel?.ActivateSubsession(subsession);
    }

    private void OnScrollViewerPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs change)
    {
        if (change.Property != ScrollViewer.OffsetProperty)
        {
            return;
        }

        OnScrollOffsetChanged();
    }

    private void OnTranscriptChanged()
    {
        if (_shouldAutoScroll)
        {
            QueueScrollToBottom();
        }
    }

    private void OnScrollOffsetChanged()
    {
        if (_isProgrammaticScroll)
        {
            return;
        }

        _shouldAutoScroll = IsNearBottom();
        if (TranscriptScrollViewer.Offset.Y <= LoadOlderThreshold)
        {
            QueueLoadOlderTranscriptRows();
        }
    }

    private void QueueLoadOlderTranscriptRows()
    {
        if (_loadOlderPending || _viewModel?.CanLoadOlderTranscriptRows != true)
        {
            return;
        }

        _loadOlderPending = true;
        var oldExtentHeight = TranscriptScrollViewer.Extent.Height;
        var oldOffset = TranscriptScrollViewer.Offset;

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                if (_viewModel is null)
                {
                    return;
                }

                var loaded = await _viewModel.LoadOlderTranscriptRowsAsync();
                if (!loaded)
                {
                    return;
                }

                Dispatcher.UIThread.Post(() =>
                {
                    var addedHeight = Math.Max(0, TranscriptScrollViewer.Extent.Height - oldExtentHeight);
                    _isProgrammaticScroll = true;
                    TranscriptScrollViewer.Offset = new Vector(oldOffset.X, oldOffset.Y + addedHeight);
                    _isProgrammaticScroll = false;
                    _shouldAutoScroll = false;
                }, DispatcherPriority.Background);
            }
            finally
            {
                _loadOlderPending = false;
            }
        }, DispatcherPriority.Background);
    }

    private void QueueScrollToBottom()
    {
        if (_scrollToBottomPending)
        {
            return;
        }

        _scrollToBottomPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            _scrollToBottomPending = false;
            ScrollToBottom();
        }, DispatcherPriority.Background);
    }

    private void ScrollToBottom()
    {
        var maxOffsetY = Math.Max(0, TranscriptScrollViewer.Extent.Height - TranscriptScrollViewer.Viewport.Height);
        _isProgrammaticScroll = true;
        TranscriptScrollViewer.Offset = new Vector(TranscriptScrollViewer.Offset.X, maxOffsetY);
        _isProgrammaticScroll = false;
        _shouldAutoScroll = true;
    }

    private bool IsNearBottom()
    {
        var distanceFromBottom = TranscriptScrollViewer.Extent.Height - (TranscriptScrollViewer.Offset.Y + TranscriptScrollViewer.Viewport.Height);
        return distanceFromBottom <= AutoScrollThreshold;
    }
}
