using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sunder.Package.Agent.Execution.Docker;

public partial class DockerExecutionSettingsView : UserControl, IDisposable
{
    private DockerExecutionSettingsViewModel? _viewModel;

    public DockerExecutionSettingsView()
    {
        InitializeComponent();
    }

    public DockerExecutionSettingsView(DockerExecutionSettingsViewModel viewModel)
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

    private async void OnChooseDockerCliClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || DataContext is not DockerExecutionSettingsViewModel viewModel)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select Docker CLI executable",
            AllowMultiple = false,
        });
        var file = files.FirstOrDefault();
        if (file is not null)
        {
            viewModel.ApplySelectedDockerCliPath(file.Path.LocalPath);
        }
    }
}
