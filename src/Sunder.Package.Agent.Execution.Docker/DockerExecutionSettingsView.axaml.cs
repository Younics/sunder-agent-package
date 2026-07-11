using Avalonia.Controls;

namespace Sunder.Package.Agent.Execution.Docker;

public partial class DockerExecutionSettingsView : UserControl, IDisposable
{
    private DockerExecutionSettingsViewModel? _viewModel;

    public DockerExecutionSettingsView()
    {
        InitializeComponent();
    }

    public DockerExecutionSettingsView(DockerExecutionSettingsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public void Dispose()
    {
        DataContext = null;
        _viewModel?.Dispose();
        _viewModel = null;
    }
}
