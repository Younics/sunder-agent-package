using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubsessionsView : UserControl, IDisposable
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private readonly TranscriptViewBehavior _transcriptBehavior;
    private SubsessionsViewModel? _viewModel;
    private bool _disposed;

    public SubsessionsView()
    {
        InitializeComponent();
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            SubsessionAdaptiveLayout,
            SubsessionListPane,
            SubsessionDetailPane,
            isCompact =>
            {
                if (ViewModel is { } viewModel)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
        _transcriptBehavior = new TranscriptViewBehavior(
            this,
            TranscriptScrollViewer,
            TranscriptItemsControl,
            JumpToLatestTranscriptButton,
            () => ViewModel?.CanLoadOlderTranscriptRows == true,
            (anchor, cancellationToken) => ViewModel?.LoadOlderTranscriptRowsAsync(anchor, cancellationToken)
                                           ?? Task.FromResult(false),
            () => ViewModel?.CanLoadNewerTranscriptRows == true,
            (anchor, cancellationToken) => ViewModel?.LoadNewerTranscriptRowsAsync(anchor, cancellationToken)
                                           ?? Task.FromResult(false),
            () => ViewModel?.HasNewerTranscriptRows == true,
            () => ViewModel?.IsTranscriptLoading == true,
            () => ViewModel?.Messages.Count > 0,
            () => ViewModel?.HasSelectedSubsession == true,
            () => ViewModel?.DetachTranscriptFromLatest(),
            () => ViewModel?.ResumeTranscriptFollowingLatestIfCaughtUp(),
            isVisible => ViewModel?.SetTranscriptJumpToLatestVisible(isVisible),
            anchor => ViewModel?.SetTranscriptViewportAnchor(anchor),
            exception => ViewModel?.ReportTranscriptPagingFailure(exception));
    }

    public SubsessionsView(SubsessionsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        _viewModel.PropertyChanging += OnViewModelPropertyChanging;
        _viewModel.TranscriptChanging += OnTranscriptChanging;
        _viewModel.TranscriptChanged += OnTranscriptChanged;
        DataContext = viewModel;
    }

    private SubsessionsViewModel? ViewModel => _viewModel ?? DataContext as SubsessionsViewModel;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _adaptiveLayout.Dispose();
        _transcriptBehavior.Dispose();
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanging -= OnViewModelPropertyChanging;
            _viewModel.TranscriptChanging -= OnTranscriptChanging;
            _viewModel.TranscriptChanged -= OnTranscriptChanged;
            _viewModel.Dispose();
        }

        DataContext = null;
        _viewModel = null;
    }

    private void OnViewModelPropertyChanging(object? sender, PropertyChangingEventArgs e)
    {
        if (string.Equals(
                e.PropertyName,
                nameof(SubsessionsViewModel.SelectedSubsession),
                StringComparison.Ordinal))
        {
            _transcriptBehavior.MarkInitialPlacementPending();
        }
    }

    private void OnSubsessionItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SubsessionListItemViewModel subsession)
        {
            return;
        }

        var viewModel = ViewModel;
        if (viewModel?.SelectedSubsession?.SessionId != subsession.SessionId)
        {
            _transcriptBehavior.MarkInitialPlacementPending();
        }

        viewModel?.ActivateSubsession(subsession);
    }

    private void OnTranscriptChanging()
        => _transcriptBehavior.OnTranscriptChanging(
            ViewModel?.IsLoadingOlderTranscriptRows == true
            || ViewModel?.IsLoadingNewerTranscriptRows == true);

    private void OnTranscriptChanged() => _transcriptBehavior.OnTranscriptChanged();

    private void JumpToLatestTranscript_OnClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (!viewModel.HasNewerTranscriptRows)
        {
            _transcriptBehavior.ScrollToBottom();
        }
        else if (viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            _transcriptBehavior.JumpToLatest(
                () => viewModel.JumpToLatestTranscriptCommand.Execute(null));
        }
    }

    private void ToolStepHeader_OnClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SubsessionToolInvocationRowViewModel toolRow)
        {
            return;
        }

        _transcriptBehavior.MutateViewport(() =>
        {
            toolRow.ToggleExpandedCommand.Execute(null);
            ViewModel?.SetTranscriptRowExpanded(toolRow, toolRow.IsExpanded);
        });
    }
}
