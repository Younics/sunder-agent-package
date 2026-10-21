using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.LMStudio;

[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(LMStudioSettingsViewModel))]
public partial class LMStudioSettingsView : UserControl, IDisposable
{
    private LMStudioSettingsViewModel? _viewModel;
    private int _disposed;

    public LMStudioSettingsView()
    {
        InitializeComponent();
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public LMStudioSettingsView(LMStudioSettingsViewModel viewModel)
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
