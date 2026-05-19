using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubsessionsView : UserControl
{
    private const double WideSubsessionMinimumWidth = 820;

    private SubsessionsViewModel? _viewModel;
    private readonly TranscriptScrollCoordinator _transcriptScrollCoordinator;

    public SubsessionsView()
    {
        InitializeComponent();
        _transcriptScrollCoordinator = new TranscriptScrollCoordinator(
            TranscriptScrollViewer,
            () => _viewModel?.CanLoadOlderTranscriptRows == true,
            () => _viewModel?.LoadOlderTranscriptRowsAsync() ?? Task.FromResult(false),
            () => _viewModel?.CanLoadNewerTranscriptRows == true,
            () => _viewModel?.LoadNewerTranscriptRowsAsync() ?? Task.FromResult(false),
            () => _viewModel?.HasNewerTranscriptRows == true,
            isVisible => JumpToLatestTranscriptButton.IsVisible = isVisible);
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout();
            _transcriptScrollCoordinator.QueueScrollToBottom();
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

    private void OnTranscriptChanged()
        => _transcriptScrollCoordinator.OnTranscriptChanged();

    private void JumpToLatestTranscript_OnClick(object? sender, RoutedEventArgs e)
    {
        var viewModel = _viewModel ?? DataContext as SubsessionsViewModel;
        if (viewModel is null)
        {
            return;
        }

        if (!viewModel.HasNewerTranscriptRows)
        {
            _transcriptScrollCoordinator.QueueScrollToBottom();
            return;
        }

        if (!viewModel.JumpToLatestTranscriptCommand.CanExecute(null))
        {
            return;
        }

        _transcriptScrollCoordinator.ForceScrollToBottomOnNextTranscriptChanged();
        viewModel.JumpToLatestTranscriptCommand.Execute(null);
    }
}
