using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public partial class AgentWorkspacesView : UserControl
{
    private const double WideWorkspaceMinimumWidth = 820;

    private AgentWorkspacesViewModel? _viewModel;

    public AgentWorkspacesView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            ApplyResponsiveLayout();
        };
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    public AgentWorkspacesView(
        AgentWorkspaceService workspaceService,
        AgentExecutionTargetService targetService,
        IPackageExtensionCatalog extensionCatalog,
        AgentExecutionTargetWarmupService warmupService)
        : this()
    {
        _viewModel = new AgentWorkspacesViewModel(workspaceService, targetService, extensionCatalog, warmupService);
        DataContext = _viewModel;
    }

    private void ApplyResponsiveLayout()
    {
        var useCompactLayout = Bounds.Width > 0 && Bounds.Width < WideWorkspaceMinimumWidth;
        var viewModel = _viewModel ?? DataContext as AgentWorkspacesViewModel;
        if (viewModel is not null)
        {
            viewModel.IsCompactLayout = useCompactLayout;
        }

        WorkspaceAdaptiveLayout.ColumnSpacing = useCompactLayout ? 0 : 4;
        WorkspaceListPane.BorderThickness = useCompactLayout ? new Thickness(0) : new Thickness(0, 0, 1, 0);

        Grid.SetColumn(WorkspaceListPane, 0);
        Grid.SetColumn(WorkspaceEditorPane, useCompactLayout ? 0 : 1);
        Grid.SetColumnSpan(WorkspaceListPane, useCompactLayout ? 2 : 1);
        Grid.SetColumnSpan(WorkspaceEditorPane, useCompactLayout ? 2 : 1);
    }

    private async void OnAddEditorPathItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not AgentEditorPathListFieldViewModel field)
        {
            return;
        }

        if (!field.UseFolderPicker)
        {
            field.AddDefaultItem();
            return;
        }

        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select allowed root",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            field.AddItem(folder.Path.LocalPath);
        }
    }

    private static void OnSetSelectedEditorPathItemAsDefaultClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AgentEditorPathListFieldViewModel field)
        {
            field.SetSelectedItemAsDefault();
        }
    }

    private static void OnDeleteSelectedEditorPathItemClick(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is AgentEditorPathListFieldViewModel field)
        {
            field.DeleteSelectedItem();
        }
    }
}
