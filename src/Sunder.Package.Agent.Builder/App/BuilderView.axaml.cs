using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Builder;

[Sunder.Sdk.Packaging.SunderPackageView("younics.sunder.package.maker", "Sunder Package Maker",
    IconPath = "Assets/builder-icon.png", DefaultPlacement = PackageViewPlacement.Middle)]
public partial class BuilderView : UserControl, IDisposable, IPackageViewWarmupTarget, IPackageViewNavigationTarget
{
    private readonly AdaptiveMasterDetail _adaptiveLayout;
    private PresentationOperationObserver _operations;
    private bool _disposed;
    private BuilderViewModel? Model => DataContext as BuilderViewModel;

    public BuilderView()
    {
        _operations = PresentationOperationObserver.CreateUnreported("Sunder Package Maker view");
        InitializeComponent();
        _adaptiveLayout = new(this, BuilderAdaptiveLayout, BuilderListPane, BuilderEditorPane,
            compact => { if (Model is { } model) model.IsCompactLayout = compact; });
    }

    public BuilderView(BuilderViewModel viewModel) : this() => DataContext = viewModel;

    [ActivatorUtilitiesConstructor]
    public BuilderView(BuilderViewModel viewModel, IPackageContext packageContext, IPackageNotificationService notifications) : this(viewModel)
    {
        _operations.Dispose();
        _operations = new(packageContext, notifications, "Sunder Package Maker view");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _operations.Dispose();
        _adaptiveLayout.Dispose();
        DataContext = null;
    }

    public async ValueTask WarmupAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Model is { } model) await model.InitializeAsync(cancellationToken);
    }
    public async ValueTask OnNavigatedToAsync(PackageViewNavigationContext context, CancellationToken cancellationToken = default)
    {
        await WarmupAsync(cancellationToken);
        if (Model is { } model) await model.RefreshSelectedProjectAsync().WaitAsync(cancellationToken);
    }

    private void Submit(string name, Func<BuilderViewModel, Task> action)
    {
        if (Model is { } model) _operations.Submit(new(name, "The package operation could not be completed."), _ => action(model));
    }
    private void OnCreateProjectClicked(object? sender, RoutedEventArgs e) => Submit("builder.create", model => model.CreateProjectAsync());
    private void OnDotnetClicked(object? sender, RoutedEventArgs e) => Submit("builder.dotnet", model => model.SelectTechnologyAsync(BuilderProjectKind.Dotnet));
    private void OnNodeClicked(object? sender, RoutedEventArgs e) => Submit("builder.node", model => model.SelectTechnologyAsync(BuilderProjectKind.Node));
    private void OnRetrySdkClicked(object? sender, RoutedEventArgs e) => Submit("builder.retry-sdk", model => model.RetryCreationSdkAsync());
    private void OnCompleteCreationClicked(object? sender, RoutedEventArgs e) => Submit("builder.create-source", model => model.CompleteCreationAsync());
    private void OnLoadClicked(object? sender, RoutedEventArgs e) => Submit("builder.load", model => model.LoadSelectedProjectAsync());
    private void OnUnloadClicked(object? sender, RoutedEventArgs e) => Submit("builder.unload", model => model.UnloadSelectedProjectAsync());
    private void OnBuildClicked(object? sender, RoutedEventArgs e) => Submit("builder.build", model => model.BuildSelectedProjectAsync());
    private void OnArchiveClicked(object? sender, RoutedEventArgs e) => Submit("builder.archive", model => model.CreatePackageArchiveAsync());
    private void OnRetrySetupClicked(object? sender, RoutedEventArgs e) => Submit("builder.setup", model => model.RetrySelectedSetupAsync());
    private void OnSaveMetadataClicked(object? sender, RoutedEventArgs e) => Submit("builder.save-metadata", model => model.SaveSelectedMetadataAsync());
    private void OnRemoveClicked(object? sender, RoutedEventArgs e) => Submit("builder.remove", model => model.DeleteSelectedProjectAsync());
    private void OnLogsClicked(object? sender, RoutedEventArgs e) => Submit("builder.logs", model => model.OpenPackageLogsAsync());
    private void OnManageSdksClicked(object? sender, RoutedEventArgs e) => Submit("builder.settings", model => model.OpenSdkSettingsAsync());
    private void OnOpenCandidateClicked(object? sender, RoutedEventArgs e) => Submit("builder.open-candidate", model => model.OpenSelectedCandidateAsync());
    private void OnBackClicked(object? sender, RoutedEventArgs e) => Model?.BackToProjectList();
    private void OnBackToTechnologyClicked(object? sender, RoutedEventArgs e) => Model?.BackToTechnology();
    private void OnDiscardClicked(object? sender, RoutedEventArgs e) => Model?.DiscardSelectedMetadata();
    private void OnCancelCreationClicked(object? sender, RoutedEventArgs e) => Model?.CancelCreation();
    private void OnCancelOperationClicked(object? sender, RoutedEventArgs e) => Model?.CancelCurrentOperation();
    private void OnWatchClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is CheckBox check) Submit("builder.watch", model => model.SetSelectedWatchAsync(check.IsChecked == true));
    }
    private void OnProjectItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is Control { DataContext: BuilderProjectViewModel row }) Model?.ActivateProject(row);
    }
    private void OnOpenFolderClicked(object? sender, RoutedEventArgs e) => PickFolder("Open package folder", (model, folder) => model.OpenFolderAsync(folder));
    private void OnLocateClicked(object? sender, RoutedEventArgs e)
    {
        if (Model?.SelectedProject is { } row) PickFolder("Locate package source", (model, folder) => model.LocateProjectAsync(row, folder));
    }
    private void OnBrowseParentClicked(object? sender, RoutedEventArgs e) => PickFolder("Choose parent folder", (model, folder) =>
    {
        if (!model.Creation.IsBusy) model.Creation.ParentFolder = folder;
        return Task.CompletedTask;
    });

    private void PickFolder(string title, Func<BuilderViewModel, string, Task> apply)
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage || Model is not { } model) return;
        _operations.Submit(new("builder.pick-folder", "The local folder could not be selected."), async token =>
        {
            var folders = await storage.OpenFolderPickerAsync(new() { Title = title, AllowMultiple = false });
            token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(Model, model)) return;
            if (folders.FirstOrDefault() is { } folder)
            {
                using (folder)
                {
                    var localPath = folder.TryGetLocalPath() ?? throw new InvalidOperationException("Choose a local filesystem folder.");
                    await apply(model, localPath);
                }
            }
        });
    }

    private void OnRevealClicked(object? sender, RoutedEventArgs e)
    {
        if (Model?.SelectedProject is not { } row || TopLevel.GetTopLevel(this)?.Launcher is not { } launcher) return;
        var folder = row.ProjectFolder;
        _operations.Submit(new("builder.open-source-folder", "The source folder could not be opened."), async token =>
        {
            token.ThrowIfCancellationRequested();
            if (!Path.IsPathFullyQualified(folder) || !await launcher.LaunchDirectoryInfoAsync(new(folder)))
                throw new IOException("The local source folder is unavailable.");
        });
    }
}
