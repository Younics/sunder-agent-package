using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.OpenAI;

public partial class OpenAiSettingsView : UserControl
{
    public OpenAiSettingsView()
    {
        InitializeComponent();
    }

    public OpenAiSettingsView(OpenAiSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
