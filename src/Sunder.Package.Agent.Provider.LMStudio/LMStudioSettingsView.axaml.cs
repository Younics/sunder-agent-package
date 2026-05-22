using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.LMStudio;

public partial class LMStudioSettingsView : UserControl
{
    public LMStudioSettingsView()
    {
        InitializeComponent();
    }

    public LMStudioSettingsView(LMStudioSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
