using Avalonia.Controls;

namespace Sunder.Package.Agent.Execution.Docker;

public partial class DockerExecutionSettingsView : UserControl
{
    public DockerExecutionSettingsView()
    {
        InitializeComponent();
    }

    public DockerExecutionSettingsView(DockerExecutionSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
