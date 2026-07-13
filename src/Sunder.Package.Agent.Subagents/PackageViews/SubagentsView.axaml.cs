using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Models;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public partial class SubagentsView : UserControl, IDisposable
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private readonly PresentationTaskScope _tasks = new();
    private SubagentsViewModel? _viewModel;
    private bool _disposed;

    public SubagentsView()
    {
        InitializeComponent();
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            SubagentAdaptiveLayout,
            SubagentListPane,
            SubagentEditorPane,
            isCompact =>
            {
                var viewModel = _viewModel ?? DataContext as SubagentsViewModel;
                if (viewModel is not null)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
    }

    public SubagentsView(SubagentsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _adaptiveLayout.Dispose();
        _tasks.Dispose();
        _viewModel?.Dispose();
        DataContext = null;
        _viewModel = null;
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
        _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    SubagentDisplayNameTextBox.Focus();
                }
            }, DispatcherPriority.Background);
        });
    }
}
