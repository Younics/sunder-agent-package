using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.OpenAI;

[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(OpenAiSettingsViewModel))]
public partial class OpenAiSettingsView : UserControl, IDisposable
{
    private OpenAiSettingsViewModel? _viewModel;
    private int _disposed;

    public OpenAiSettingsView()
    {
        InitializeComponent();
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public OpenAiSettingsView(OpenAiSettingsViewModel viewModel)
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
