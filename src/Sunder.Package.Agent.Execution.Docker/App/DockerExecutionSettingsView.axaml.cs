using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Execution.Docker;

[Sunder.Sdk.Packaging.SunderPackageSettingsView(ViewModel = typeof(DockerExecutionSettingsViewModel))]
public partial class DockerExecutionSettingsView : UserControl, IDisposable
{
    private DockerExecutionSettingsViewModel? _viewModel;
    private PresentationOperationObserver _operations;
    private bool _disposed;

    public DockerExecutionSettingsView()
    {
        _operations = PresentationOperationObserver.CreateUnreported("Docker executable picker");
        InitializeComponent();
    }

    public DockerExecutionSettingsView(DockerExecutionSettingsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    public DockerExecutionSettingsView(
        DockerExecutionSettingsViewModel viewModel,
        IPackageContext packageContext,
        IPackageNotificationService notifications)
        : this(viewModel)
    {
        _operations.Dispose();
        _operations = new PresentationOperationObserver(
            packageContext,
            notifications,
            "Docker executable picker");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _operations.Dispose();
        DataContext = null;
        _viewModel?.Dispose();
        _viewModel = null;
    }

    private void OnChooseDockerCliClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        var topLevel = TopLevel.GetTopLevel(this);
        if (topLevel?.StorageProvider is null
            || DataContext is not DockerExecutionSettingsViewModel { CanMutate: true } viewModel)
        {
            return;
        }

        _operations.Submit(
            new PresentationOperation(
                "docker-execution.pick-cli",
                "The Docker CLI executable could not be selected."),
            async cancellationToken =>
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select Docker CLI executable",
                    AllowMultiple = false,
                });
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals(DataContext, viewModel))
                {
                    return;
                }

                var file = files.FirstOrDefault();
                if (file is not null)
                {
                    viewModel.ApplySelectedDockerCliPath(file.Path.LocalPath);
                }
            });
    }
}
