using Avalonia.Controls;

namespace Sunder.Package.Agent.Execution.Docker;

public partial class DockerExecutionSettingsView : UserControl, IDisposable
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

    public void Dispose()
    {
        if (DataContext is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}
