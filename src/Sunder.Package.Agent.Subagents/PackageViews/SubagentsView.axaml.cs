using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubagentsView : UserControl
{
    private const double WideSubagentMinimumWidth = 820;

    private SubagentsViewModel? _viewModel;

    public SubagentsView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public SubagentsView(SubagentsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
        _ = viewModel.InitializeAsync();
    }

    private void ApplyResponsiveLayout()
    {
        var useCompactLayout = Bounds.Width > 0 && Bounds.Width < WideSubagentMinimumWidth;
        var viewModel = _viewModel ?? DataContext as SubagentsViewModel;
        if (viewModel is not null)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }

        SubagentAdaptiveLayout.ColumnSpacing = useCompactLayout ? 0 : 4;
        SubagentListPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(SubagentListPane, 0);
        Grid.SetColumn(SubagentEditorPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(SubagentListPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(SubagentEditorPane, useCompactLayout ? 2 : 1);
    }

    private void OnSubagentItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not SubagentRecord subagent)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as SubagentsViewModel;
        viewModel?.ActivateSubagent(subagent);
        if (viewModel?.IsCompactLayout == true)
        {
            FocusSubagentDisplayName();
        }
    }

    private void FocusSubagentDisplayName()
    {
        Dispatcher.UIThread.Post(
            () => SubagentDisplayNameTextBox.Focus(),
            DispatcherPriority.Background);
    }
}
