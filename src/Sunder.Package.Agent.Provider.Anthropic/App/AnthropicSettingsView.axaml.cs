using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.Anthropic;

[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(AnthropicSettingsViewModel))]
public partial class AnthropicSettingsView : UserControl, IDisposable
{
    private AnthropicSettingsViewModel? _viewModel;
    private int _disposed;

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
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        DataContext = null;
        _viewModel?.Dispose();
        _viewModel = null;
    }
}
