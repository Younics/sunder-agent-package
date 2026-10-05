using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Builder;

// The constructor assigns the activation-owned Builder model to DataContext.
[Sunder.Sdk.Packaging.SunderPackageView("sunder.package.agent.builder", "Package Builder",
    IconPath = "Assets/builder-icon.png", DefaultPlacement = PackageViewPlacement.Middle)]
public partial class BuilderView : UserControl, IDisposable, IPackageViewWarmupTarget, IPackageViewNavigationTarget
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private readonly PresentationTaskScope _tasks = new(
        PresentationTaskScope.IgnoreExpectedFailure);
    private PresentationOperationObserver _operations;
    private BuilderViewModel? _viewModel;
    private bool _disposed;

    public BuilderView()
    {
        _operations = PresentationOperationObserver.CreateUnreported("Package Builder view");
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

    [ActivatorUtilitiesConstructor]
    public BuilderView(
        BuilderViewModel viewModel,
        IPackageContext packageContext,
        IPackageNotificationService notifications)
        : this(viewModel)
    {
        _operations.Dispose();
        _operations = new PresentationOperationObserver(
            packageContext,
            notifications,
            "Package Builder view");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _operations.Dispose();
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

    private void OnRefreshSetupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            Submit("builder-view.refresh-setup", viewModel.RefreshSetupAsync);
        }
    }

    private void OnCreateProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            Submit("builder-view.create-project", async () =>
            {
                await viewModel.CreateProjectAsync();
                FocusPackageDisplayName();
            });
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

    private void OnPickProjectFolderClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null || DataContext is not BuilderViewModel viewModel)
        {
            return;
        }

        _operations.Submit(
            ViewOperation("pick-project-folder"),
            async cancellationToken =>
            {
                var folders = await topLevel.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
                {
                    Title = "Select package project folder",
                    AllowMultiple = false,
                });
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(DataContext, viewModel))
                {
                    return;
                }

                var folder = folders.FirstOrDefault();
                if (folder is not null)
                {
                    viewModel.ApplySelectedFolder(folder.Path.LocalPath);
                    FocusPackageDisplayName();
                }
            });
    }

    private void OnDeleteProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            Submit("builder-view.delete-project", viewModel.DeleteSelectedProjectAsync);
        }
    }

    private void OnInitializeProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            Submit("builder-view.initialize-project", viewModel.InitializeSelectedProjectAsync);
        }
    }

    private void OnBuildProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            Submit("builder-view.build-project", viewModel.BuildSelectedProjectAsync);
        }
    }

    private void OnEnsureSetupClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            Submit("builder-view.ensure-setup", viewModel.EnsureSelectedProjectSetupAsync);
        }
    }

    private void OnPublishProjectClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (DataContext is BuilderViewModel viewModel)
        {
            Submit("builder-view.publish-project", viewModel.PublishSelectedProjectAsync);
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

    private void Submit(string operationName, Func<Task> action)
        => _operations.Submit(
            ViewOperation(operationName),
            _ => action());

    private static PresentationOperation ViewOperation(string operationName)
        => new(
            operationName,
            "The Package Builder operation could not be completed.");
}
