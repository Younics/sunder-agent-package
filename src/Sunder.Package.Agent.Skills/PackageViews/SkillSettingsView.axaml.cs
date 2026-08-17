using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Skills.PackageViews;

public partial class SkillSettingsView : UserControl, IDisposable
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private PresentationOperationObserver _operations;
    private SkillSettingsViewModel? _viewModel;
    private bool _disposed;

    public SkillSettingsView()
    {
        _operations = PresentationOperationObserver.CreateUnreported("Skills picker");
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

    public SkillSettingsView(
        SkillSettingsViewModel viewModel,
        IPackageContext packageContext,
        IPackageNotificationService notifications)
        : this(viewModel)
    {
        _operations.Dispose();
        _operations = new PresentationOperationObserver(
            packageContext,
            notifications,
            "Skills picker");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operations.Dispose();
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

    private void OnAddLocalFolderClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

        SubmitLocalFolderImport(
            viewModel,
            PickLocalFolderAsync(topLevel.StorageProvider));
    }

    internal void SubmitLocalFolderImport(
        SkillSettingsViewModel viewModel,
        Task<string?> folderPathTask)
        => _operations.Submit(
            new PresentationOperation(
                "skills.pick-local-folder",
                "The local skill folder could not be selected or imported."),
            _ => ContinueLocalFolderImportAsync(
                viewModel,
                folderPathTask));

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
