using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.Gemini;

public partial class GeminiSettingsView : UserControl
{
    public GeminiSettingsView()
    {
        InitializeComponent();
    }

    public GeminiSettingsView(GeminiSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
