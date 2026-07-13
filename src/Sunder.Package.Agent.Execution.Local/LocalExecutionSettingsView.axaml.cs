using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sunder.Package.Agent.Execution.Local;

public partial class LocalExecutionSettingsView : UserControl
{
    public LocalExecutionSettingsView()
    {
        InitializeComponent();
    }

    public LocalExecutionSettingsView(LocalExecutionSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private async void OnChooseShellExecutableClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: LocalShellRowViewModel shell } || !shell.CanEdit)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Select shell executable",
            AllowMultiple = false,
        });
        var file = files.FirstOrDefault();
        if (file is not null)
        {
            shell.ApplySelectedExecutablePath(file.Path.LocalPath);
        }
    }
}
