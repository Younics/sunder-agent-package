using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubsessionsView : UserControl
{
    private const double WideSubsessionMinimumWidth = 820;

    private SubsessionsViewModel? _viewModel;
    private TranscriptScrollCoordinator? _transcriptScrollCoordinator;
    private bool _transcriptChangedBeforeScrollReady;
    private bool _initialTranscriptPlacementPending = true;
    private bool _initialTranscriptPlacementQueued;
    private int _initialTranscriptPlacementVersion;

    public SubsessionsView()
    {
        InitializeComponent();
        HideTranscriptUntilInitialPlacement();
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout();
            if (EnsureTranscriptScrollCoordinator())
            {
                HandleTranscriptReadyAfterScrollReady();
                return;
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (EnsureTranscriptScrollCoordinator())
                {
                    HandleTranscriptReadyAfterScrollReady();
                }
            }, DispatcherPriority.Loaded);
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public SubsessionsView(SubsessionsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanging += OnViewModelPropertyChanging;
        _viewModel.TranscriptChanging += OnTranscriptChanging;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        DataContext = viewModel;
        _ = viewModel.InitializeAsync();
    }

    private void OnViewModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SubsessionsViewModel.SelectedSubsession), StringComparison.Ordinal))
        {
            MarkInitialTranscriptPlacementPending();
        }
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
        if (viewModel?.SelectedSubsession?.SessionId != subsession.SessionId)
        {
            MarkInitialTranscriptPlacementPending();
        }

        viewModel?.ActivateSubsession(subsession);
    }

    private void OnTranscriptChanged()
    {
        if (!EnsureTranscriptScrollCoordinator())
        {
            _transcriptChangedBeforeScrollReady = true;
            return;
        }

        if (TryHandleInitialTranscriptPlacement())
        {
            return;
        }

        _transcriptScrollCoordinator?.OnTranscriptChanged();
    }

    private void OnTranscriptChanging()
    {
        if (_initialTranscriptPlacementPending)
        {
            return;
        }

        if (!EnsureTranscriptScrollCoordinator())
        {
            return;
        }

        _transcriptScrollCoordinator?.BeginTranscriptMutation();
    }

    private void JumpToLatestTranscript_OnClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = _viewModel ?? DataContext as SubsessionsViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (!viewModel.HasNewerTranscriptRows)
        {
            _transcriptScrollCoordinator?.QueueScrollToBottom();
            return;
        }

        if (!viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            return;
        }

        _transcriptScrollCoordinator?.ForceScrollToBottomOnNextTranscriptChanged();
        viewModel.JumpToLatestTranscriptCommand.Execute(null);
    }

    private bool EnsureTranscriptScrollCoordinator()
    {
        if (_transcriptScrollCoordinator is not null)
        {
            return true;
        }

        _transcriptScrollCoordinator = new TranscriptScrollCoordinator(
            TranscriptScrollViewer,
            TranscriptItemsControl,
            () => _viewModel?.CanLoadOlderTranscriptRows == true,
            () => _viewModel?.LoadOlderTranscriptRowsAsync() ?? Task.FromResult(false),
            () => _viewModel?.CanLoadNewerTranscriptRows == true,
            () => _viewModel?.LoadNewerTranscriptRowsAsync() ?? Task.FromResult(false),
            () => _viewModel?.HasNewerTranscriptRows == true,
            isVisible => JumpToLatestTranscriptButton.IsVisible = isVisible);
        return true;
    }

    private void HandleTranscriptReadyAfterScrollReady()
    {
        if (TryHandleInitialTranscriptPlacement())
        {
            return;
        }

        if (_transcriptChangedBeforeScrollReady)
        {
            _transcriptChangedBeforeScrollReady = false;
            _transcriptScrollCoordinator?.OnTranscriptChanged();
        }
    }

    private bool TryHandleInitialTranscriptPlacement()
    {
        if (!_initialTranscriptPlacementPending)
        {
            return false;
        }

        var viewModel = _viewModel ?? DataContext as SubsessionsViewModel;
        _transcriptChangedBeforeScrollReady = false;
        if (viewModel is null || !viewModel.HasSelectedSubsession || viewModel.Messages.Count == 0 || !TranscriptScrollViewer.IsVisible)
        {
            CompleteInitialTranscriptPlacement(_initialTranscriptPlacementVersion);
            return true;
        }

        if (_initialTranscriptPlacementQueued)
        {
            return true;
        }

        _initialTranscriptPlacementQueued = true;
        var placementVersion = _initialTranscriptPlacementVersion;
        HideTranscriptUntilInitialPlacement();
        _transcriptScrollCoordinator?.QueueScrollToBottomAfterLayoutSettles(() => CompleteInitialTranscriptPlacement(placementVersion));
        return true;
    }

    private void MarkInitialTranscriptPlacementPending()
    {
        if (!_initialTranscriptPlacementPending || _initialTranscriptPlacementQueued)
        {
            _initialTranscriptPlacementVersion++;
        }

        _initialTranscriptPlacementPending = true;
        _initialTranscriptPlacementQueued = false;
        HideTranscriptUntilInitialPlacement();
    }

    private void HideTranscriptUntilInitialPlacement()
        => TranscriptScrollViewer.Opacity = 0;

    private void CompleteInitialTranscriptPlacement(int placementVersion)
    {
        if (placementVersion != _initialTranscriptPlacementVersion)
        {
            return;
        }

        _initialTranscriptPlacementPending = false;
        _initialTranscriptPlacementQueued = false;
        TranscriptScrollViewer.Opacity = 1;
    }

    private void ToolStepHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SubsessionToolInvocationRowViewModel toolRow)
        {
            return;
        }

        if (EnsureTranscriptScrollCoordinator())
        {
            _transcriptScrollCoordinator?.BeginViewportMutation();
        }

        toolRow.ToggleExpandedCommand.Execute(null);
        _transcriptScrollCoordinator?.OnViewportContentChanged();
    }
}
