using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

public partial class BuilderView : UserControl, IDisposable, IPackageViewWarmupTarget, IPackageViewNavigationTarget
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private readonly PresentationTaskScope _tasks = new();
    private BuilderViewModel? _viewModel;
    private bool _disposed;

    public BuilderView()
    {
        InitializeComponent();
        _adaptiveLayout = new AdaptiveMasterDetail(
            this,
            BuilderAdaptiveLayout,
            BuilderListPane,
            BuilderEditorPane,
            isCompact =>
            {
                var viewModel = _viewModel ?? DataContext as BuilderViewModel;
                if (viewModel is not null)
                {
                    viewModel.IsCompactLayout = isCompact;
                }
            });
    }

    public BuilderView(BuilderViewModel viewModel)
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
        _tasks.Dispose();
        _adaptiveLayout.Dispose();
        DataContext = null;
        _viewModel = null;
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if ((_viewModel ?? DataContext as BuilderViewModel) is { } viewModel)
        {
            await viewModel.InitializeAsync(cancellationToken);
        }
    }

    public ValueTask OnNavigatedToAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
        => WarmupAsync(cancellationToken);

    private async void OnRefreshSetupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            await viewModel.RefreshSetupAsync();
        }
    }

    private async void OnCreateProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            await viewModel.CreateProjectAsync();
            FocusPackageDisplayName();
        }
    }

    private void OnProjectItemTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not BuilderProjectViewModel project)
        {
            return;
        }

        var viewModel = _viewModel ?? DataContext as BuilderViewModel;
        viewModel?.ActivateProject(project);
        if (viewModel?.IsCompactLayout == true)
        {
            FocusPackageDisplayName();
        }
    }

    private async void OnPickProjectFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || DataContext is not BuilderViewModel viewModel)
        {
            return;
        }

        var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Select package project folder",
            AllowMultiple = false,
        });

        var folder = folders.FirstOrDefault();
        if (folder is not null)
        {
            viewModel.ApplySelectedFolder(folder.Path.LocalPath);
            FocusPackageDisplayName();
        }
    }

    private async void OnDeleteProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            await viewModel.DeleteSelectedProjectAsync();
        }
    }

    private async void OnInitializeProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            await viewModel.InitializeSelectedProjectAsync();
        }
    }

    private async void OnBuildProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            await viewModel.BuildSelectedProjectAsync();
        }
    }

    private async void OnEnsureSetupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            await viewModel.EnsureSelectedProjectSetupAsync();
        }
    }

    private async void OnPublishProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            await viewModel.PublishSelectedProjectAsync();
        }
    }

    private void OnBackToProjectListClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        (DataContext as BuilderViewModel)?.BackToProjectList();
    }

    private void FocusPackageDisplayName()
    {
        _tasks.Run(async _ =>
            await Dispatcher.UIThread.InvokeAsync(
                () => PackageDisplayNameTextBox.Focus(),
                DispatcherPriority.Background));
    }
}
