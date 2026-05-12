using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform.Storage;

namespace Sunder.Package.Agent.Skills.PackageViews;

public partial class SkillSettingsView : UserControl
{
    private const double WideSkillMinimumWidth = 820;

    public SkillSettingsView()
    {
        InitializeComponent();
        Loaded += (_, _) => ApplyResponsiveLayout();
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public SkillSettingsView(SkillSettingsViewModel viewModel)
        : this()
    {
        DataContext = viewModel;
    }

    private void ApplyResponsiveLayout()
    {
        var useCompactLayout = Bounds.Width > 0 && Bounds.Width < WideSkillMinimumWidth;
        if (DataContext is SkillSettingsViewModel viewModel)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }

        SkillAdaptiveLayout.ColumnSpacing = useCompactLayout ? 0 : 4;
        SkillListPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(SkillListPane, 0);
        Grid.SetColumn(SkillDetailPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(SkillListPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(SkillDetailPane, useCompactLayout ? 2 : 1);
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
