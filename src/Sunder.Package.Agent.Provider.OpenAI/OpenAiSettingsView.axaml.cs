using Avalonia.Controls;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Provider.OpenAI;

public partial class OpenAiSettingsView : UserControl, IDisposable
{
    private readonly PresentationTaskScope _tasks = new();
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
        _tasks.Run(viewModel.InitializeAsync);
    }

    public void Dispose()
    {
        _tasks.Dispose();
        DataContext = null;
        _viewModel?.Dispose();
        _viewModel = null;
    }
}
