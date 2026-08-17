using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;

namespace Sunder.Package.Agent.Execution.Local;

public partial class LocalExecutionSettingsView : UserControl, IDisposable
{
    private LocalExecutionSettingsViewModel? _viewModel;
    private PresentationOperationObserver _operations;
    private bool _disposed;

    public LocalExecutionSettingsView()
    {
        _operations = PresentationOperationObserver.CreateUnreported("Local executable picker");
        InitializeComponent();
    }

    public LocalExecutionSettingsView(LocalExecutionSettingsViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        DataContext = viewModel;
    }

    public LocalExecutionSettingsView(
        LocalExecutionSettingsViewModel viewModel,
        IPackageContext packageContext,
        IPackageNotificationService notifications)
        : this(viewModel)
    {
        _operations.Dispose();
        _operations = new PresentationOperationObserver(
            packageContext,
            notifications,
            "Local executable picker");
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

    private void OnChooseShellExecutableClicked(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
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

        _operations.Submit(
            new PresentationOperation(
                "local-execution.pick-shell",
                "The shell executable could not be selected."),
            async cancellationToken =>
            {
                var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
                {
                    Title = "Select shell executable",
                    AllowMultiple = false,
                });
                cancellationToken.ThrowIfCancellationRequested();
                if (_disposed || !ReferenceEquals((sender as Control)?.DataContext, shell))
                {
                    return;
                }

                var file = files.FirstOrDefault();
                if (file is not null)
                {
                    shell.ApplySelectedExecutablePath(file.Path.LocalPath);
                }
            });
    }
}
