using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.Anthropic;

public partial class AnthropicSettingsView : UserControl, IDisposable
{
    private AnthropicSettingsViewModel? _viewModel;

    public AnthropicSettingsView()
    {
        InitializeComponent();
    }

    public AnthropicSettingsView(AnthropicSettingsViewModel viewModel)
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
