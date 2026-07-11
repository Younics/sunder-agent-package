using Avalonia.Controls;

namespace Sunder.Package.Agent.Execution.Local;

public partial class LocalExecutionSettingsView : UserControl
{
    public LocalExecutionSettingsView()
    {
        InitializeComponent();
    }

    public LocalExecutionSettingsView(LocalExecutionSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
