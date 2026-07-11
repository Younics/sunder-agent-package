using Avalonia.Controls;

namespace Sunder.Package.Agent.Provider.OpenAI;

public partial class OpenAiSettingsView : UserControl, IDisposable
{
    private OpenAiSettingsViewModel? _viewModel;

    public OpenAiSettingsView()
    {
        InitializeComponent();
    }

    public OpenAiSettingsView(OpenAiSettingsViewModel viewModel)
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
