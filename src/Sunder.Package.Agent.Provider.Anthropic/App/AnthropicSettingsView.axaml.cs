using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.Anthropic;

[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(AnthropicSettingsViewModel))]
public partial class AnthropicSettingsView : UserControl, IDisposable
{
    private AnthropicSettingsViewModel? _viewModel;

    public AnthropicSettingsView()
    {
        InitializeComponent();
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
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
