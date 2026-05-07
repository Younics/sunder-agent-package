using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sunder.Package.Agent.Skills.PackageViews;

public partial class SkillSettingsView : UserControl
{
    public SkillSettingsView()
    {
        InitializeComponent();
    }

    public SkillSettingsView(SkillSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private async void OnAddLocalFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is not SkillSettingsViewModel viewModel)
        {
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Agent Skill Folder",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            await viewModel.ImportLocalFolderAsync(folder.Path.LocalPath);
        }
    }
}
