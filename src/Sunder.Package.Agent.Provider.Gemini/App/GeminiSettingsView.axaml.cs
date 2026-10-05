using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.Gemini;

[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(GeminiSettingsViewModel))]
public partial class GeminiSettingsView : UserControl, IDisposable
{
    private GeminiSettingsViewModel? _viewModel;

    public GeminiSettingsView()
    {
        InitializeComponent();
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public GeminiSettingsView(GeminiSettingsViewModel viewModel)
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
