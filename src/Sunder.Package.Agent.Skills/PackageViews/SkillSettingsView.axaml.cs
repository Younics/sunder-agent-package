using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Skills.PackageViews;

public partial class SkillSettingsView : UserControl, IDisposable
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private SkillSettingsViewModel? _viewModel;
    private bool _disposed;

    public SkillSettingsView()
    {
        InitializeComponent();
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            SkillAdaptiveLayout,
            SkillListPane,
            SkillDetailPane,
            isCompact =>
            {
                if (DataContext is SkillSettingsViewModel viewModel)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
    }

    public SkillSettingsView(SkillSettingsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _adaptiveLayout.Dispose();
        _viewModel?.Dispose();
        DataContext = null;
        _viewModel = null;
    }

    private void OnSkillItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not InstalledSkillItemViewModel skill)
        {
            return;
        }

        if (DataContext is SkillSettingsViewModel viewModel)
        {
            viewModel.ActivateSkill(skill);
        }
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

        await ContinueLocalFolderImportAsync(viewModel, PickLocalFolderAsync(topLevel.StorageProvider));
    }

    private static async Task<string?> PickLocalFolderAsync(IStorageProvider storageProvider)
    {
        var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select Agent Skill Folder",
            AllowMultiple = false,
        });
        return folders.FirstOrDefault()?.Path.LocalPath;
    }

    internal async Task ContinueLocalFolderImportAsync(
        SkillSettingsViewModel viewModel,
        Task<string?> folderPathTask)
    {
        try
        {
            var folderPath = await folderPathTask;
            if (_disposed
                || viewModel.IsDisposed
                || !ReferenceEquals(DataContext, viewModel)
                || string.IsNullOrWhiteSpace(folderPath))
            {
                return;
            }

            await viewModel.ImportLocalFolderAsync(folderPath);
        }
        catch (ObjectDisposedException) when (_disposed || viewModel.IsDisposed)
        {
        }
    }
}
