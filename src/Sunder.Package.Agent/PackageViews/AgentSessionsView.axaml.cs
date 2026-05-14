using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sunder.Package.Agent.Services;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentSessionsView : UserControl
{
    private const double WideSessionMinimumWidth = 820;

    private AgentSessionsViewModel? _viewModel;

    public AgentSessionsView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public AgentSessionsView(AgentSessionService sessionService)
        : this()
    {
        _viewModel = new AgentSessionsViewModel(sessionService);
        DataContext = _viewModel;
    }

    private void ApplyResponsiveLayout()
    {
        var useCompactLayout = Bounds.Width > 0 && Bounds.Width < WideSessionMinimumWidth;
        var viewModel = _viewModel ?? DataContext as AgentSessionsViewModel;
        if (viewModel is not null)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }

        SessionAdaptiveLayout.ColumnSpacing = useCompactLayout ? 0 : 4;
        SessionListPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(SessionListPane, 0);
        Grid.SetColumn(SessionEditorPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(SessionListPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(SessionEditorPane, useCompactLayout ? 2 : 1);
    }

    private void OnSessionItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AgentSessionListEntryViewModel session)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as AgentSessionsViewModel;
        viewModel?.ActivateSession(session);
        if (viewModel?.IsCompactLayout == true)
        {
            FocusSessionTitle();
        }
    }

    private void FocusSessionTitle()
    {
        Dispatcher.UIThread.Post(
            () => SessionTitleTextBox.Focus(),
            DispatcherPriority.Background);
    }
}
