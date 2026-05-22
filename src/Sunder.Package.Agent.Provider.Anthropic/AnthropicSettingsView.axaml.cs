using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.Anthropic;

public partial class AnthropicSettingsView : UserControl
{
    public AnthropicSettingsView()
    {
        InitializeComponent();
    }

    public AnthropicSettingsView(AnthropicSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }
}
