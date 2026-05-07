using Avalonia.Controls;

namespace Sunder.Package.Agent.Execution.Local;

public partial class LocalExecutionSettingsView : UserControl
{
    public LocalExecutionSettingsView()
    {
        InitializeComponent();
    }

    public LocalExecutionSettingsView(LocalShellCatalogService shellCatalogService)
        : this()
    {
        DataContext = new LocalExecutionSettingsViewModel(shellCatalogService);
    }
}
