using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.LMStudio;

public partial class LMStudioSettingsView : UserControl, IDisposable
{
    private LMStudioSettingsViewModel? _viewModel;

    public LMStudioSettingsView()
    {
        InitializeComponent();
    }

    public LMStudioSettingsView(LMStudioSettingsViewModel viewModel)
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
