using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel : ObservableObject, IDisposable
{
    internal const string ImagePullGroupKey = "sunder.package.agent.execution.docker:image-pulls";
    internal const string ImageReferenceMetadataKey = "imageReference";

    private const string TimeoutKey = "docker.timeoutSeconds.default";
    private const string DefaultTimeoutSeconds = "300";

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [TimeoutKey, DockerCli.ExecutablePathConfigurationKey];

    private readonly DockerImageCatalogService _imageCatalogService;
    private readonly IPackageContext _packageContext;
    private readonly IBackgroundProcessQueue _backgroundProcessQueue;
    private bool _suppressImageChangeNotifications;
    private bool _disposed;

    public DockerExecutionSettingsViewModel(
        DockerImageCatalogService imageCatalogService,
        IPackageContext packageContext,
        IBackgroundProcessQueue backgroundProcessQueue)
    {
        _imageCatalogService = imageCatalogService;
        _packageContext = packageContext;
        _backgroundProcessQueue = backgroundProcessQueue;
        _imageCatalogService.ImagesChanged += OnImagesChanged;
        _backgroundProcessQueue.ProcessChanged += BackgroundProcessQueue_OnProcessChanged;
        TimeoutSeconds = DefaultTimeoutSeconds;
        DockerCliPath = string.Empty;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        TimeoutSeconds = await _packageContext.Storage.State.GetValueAsync(TimeoutKey, cancellationToken)
            ?? await _packageContext.Configuration.GetValueAsync(TimeoutKey, cancellationToken)
            ?? DefaultTimeoutSeconds;
        DockerCliPath = await _packageContext.Storage.State.GetValueAsync(DockerCli.ExecutablePathConfigurationKey, cancellationToken)
            ?? await _packageContext.Configuration.GetValueAsync(DockerCli.ExecutablePathConfigurationKey, cancellationToken)
            ?? string.Empty;
        await ReloadImagesAsync(cancellationToken: cancellationToken);
    }

    public ObservableCollection<DockerImageRowViewModel> Images { get; } = [];

    public bool HasImages => Images.Count > 0;

    public bool CanAddImage => !IsBusy && !string.IsNullOrWhiteSpace(NewImageReference);

    public bool CanUseSelectedImage => !IsBusy && SelectedImage is { Status: not DockerImageStatus.Pulling };

    public bool CanPullSelectedImage => CanUseSelectedImage;

    [ObservableProperty]
    private DockerImageRowViewModel? _selectedImage;

    [ObservableProperty]
    private string _newImageReference = string.Empty;

    [ObservableProperty]
    private string _timeoutSeconds;

    [ObservableProperty]
    private string _dockerCliPath;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    partial void OnSelectedImageChanged(DockerImageRowViewModel? value)
        => NotifySelectedImageCommands();

    partial void OnNewImageReferenceChanged(string value)
        => AddImageCommand.NotifyCanExecuteChanged();

    partial void OnIsBusyChanged(bool value)
    {
        AddImageCommand.NotifyCanExecuteChanged();
        NotifySelectedImageCommands();
        RefreshAllImagesCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddImage));
        OnPropertyChanged(nameof(CanUseSelectedImage));
    }

    [RelayCommand(CanExecute = nameof(CanAddImage))]
    private async Task AddImageAsync()
    {
        try
        {
            DockerImageDefinition image;
            _suppressImageChangeNotifications = true;
            try
            {
                image = await _imageCatalogService.AddImageAsync(NewImageReference);
            }
            finally
            {
                _suppressImageChangeNotifications = false;
            }

            NewImageReference = string.Empty;
            await ReloadImagesAsync(image.ImageReference);
            StatusText = $"Added Docker image '{image.ImageReference}'. Pull it before assigning it to workspaces.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedImage))]
    private async Task DeleteSelectedImageAsync()
    {
        if (SelectedImage is null)
        {
            return;
        }

        var imageReference = SelectedImage.ImageReference;
        _suppressImageChangeNotifications = true;
        try
        {
            await _imageCatalogService.DeleteImageAsync(imageReference);
        }
        finally
        {
            _suppressImageChangeNotifications = false;
        }

        await ReloadImagesAsync();
        StatusText = $"Deleted Docker image '{imageReference}' from Sunder settings. Existing Docker images on disk were not removed.";
    }

    [RelayCommand(CanExecute = nameof(CanPullSelectedImage))]
    private void PullSelectedImage()
    {
        if (SelectedImage is null)
        {
            return;
        }

        var selected = SelectedImage;
        try
        {
            if (!IsImagePullActive(selected.ImageReference))
            {
                var imageReference = selected.ImageReference;
                _backgroundProcessQueue.Enqueue(new BackgroundProcessRequest(
                    $"Pull Docker image {imageReference}",
                    ImagePullGroupKey,
                    BackgroundProcessIndicator.Settings,
                    BackgroundProcessConcurrencyMode.SequentialWithinGroup,
                    true,
                    async context =>
                    {
                        context.ReportIndeterminate($"Pulling Docker image '{imageReference}'...");
                        var progress = new Progress<string>(line => context.ReportIndeterminate(line));
                        var result = await _imageCatalogService.PullImageAsync(imageReference, progress, context.CancellationToken).ConfigureAwait(false);
                        if (!result.Success)
                        {
                            throw new InvalidOperationException(result.Message);
                        }

                        context.ReportProgress(100, result.Message);
                    },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ImageReferenceMetadataKey] = imageReference,
                    }));
            }

            _ = ReloadImagesAsync(selected.ImageReference);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            _ = ReloadImagesAsync(selected.ImageReference);
            StatusText = ex.Message;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedImage))]
    private async Task RefreshSelectedImageAsync()
    {
        if (SelectedImage is null)
        {
            return;
        }

        var selected = SelectedImage;
        IsBusy = true;
        try
        {
            var image = await _imageCatalogService.RefreshImageAsync(selected.ImageReference);
            await ReloadImagesAsync(image.ImageReference);
            StatusText = image.LastMessage ?? $"Refreshed Docker image '{image.ImageReference}'.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBusyCommand))]
    private async Task RefreshAllImagesAsync()
    {
        IsBusy = true;
        try
        {
            await _imageCatalogService.RefreshImagesAsync();
            await ReloadImagesAsync(SelectedImage?.ImageReference);
            StatusText = "Docker image status refreshed.";
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBusyCommand))]
    private async Task SaveSettingsAsync()
    {
        if (!int.TryParse(TimeoutSeconds, out var timeoutSeconds) || timeoutSeconds <= 0)
        {
            StatusText = "Docker command timeout must be a positive number of seconds.";
            return;
        }

        IsBusy = true;
        try
        {
            await _packageContext.Storage.State.SetValueAsync(TimeoutKey, timeoutSeconds.ToString());
            TimeoutSeconds = timeoutSeconds.ToString();
            var dockerCliPath = DockerCliPath.Trim();
            if (string.IsNullOrWhiteSpace(dockerCliPath))
            {
                await _packageContext.Storage.State.DeleteValueAsync(DockerCli.ExecutablePathConfigurationKey);
            }
            else
            {
                await _packageContext.Storage.State.SetValueAsync(DockerCli.ExecutablePathConfigurationKey, dockerCliPath);
            }

            DockerCliPath = dockerCliPath;
            StatusText = "Docker execution settings saved.";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private bool CanRunBusyCommand() => !IsBusy;

    private async Task ReloadImagesAsync(
        string? selectedImageReference = null,
        CancellationToken cancellationToken = default)
    {
        var activePulls = GetActivePullImageReferences();
        Images.Clear();
        foreach (var image in await _imageCatalogService.ListImagesAsync(cancellationToken))
        {
            var displayedImage = activePulls.Contains(image.ImageReference)
                ? image with { Status = DockerImageStatus.Pulling, LastMessage = "Pulling image..." }
                : image;
            Images.Add(new DockerImageRowViewModel(displayedImage));
        }

        SelectedImage = !string.IsNullOrWhiteSpace(selectedImageReference)
            ? Images.FirstOrDefault(image => string.Equals(image.ImageReference, selectedImageReference, StringComparison.OrdinalIgnoreCase)) ?? Images.FirstOrDefault()
            : Images.FirstOrDefault();
        OnPropertyChanged(nameof(HasImages));
        NotifySelectedImageCommands();
    }

    private HashSet<string> GetActivePullImageReferences()
    {
        return _backgroundProcessQueue.ListProcesses(ImagePullGroupKey)
            .Where(process => process.IsActive)
            .Select(GetImageReference)
            .Where(imageReference => !string.IsNullOrWhiteSpace(imageReference))
            .Select(imageReference => imageReference!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private bool IsImagePullActive(string imageReference)
        => GetActivePullImageReferences().Contains(imageReference);

    private void BackgroundProcessQueue_OnProcessChanged(object? sender, BackgroundProcessChangedEventArgs e)
    {
        if (_disposed || !IsDockerImagePull(e.Snapshot))
        {
            return;
        }

        var imageReference = GetImageReference(e.Snapshot);
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed)
            {
                _ = ReloadImagesAsync(SelectedImage?.ImageReference ?? imageReference);
            }
        }, DispatcherPriority.Background);
    }

    private void OnImagesChanged()
        => RunOnUiThread(() =>
        {
            if (!_disposed && !_suppressImageChangeNotifications)
            {
                _ = ReloadImagesAsync(SelectedImage?.ImageReference);
            }
        });

    private static bool IsDockerImagePull(BackgroundProcessSnapshot snapshot)
        => string.Equals(snapshot.GroupKey, ImagePullGroupKey, StringComparison.OrdinalIgnoreCase);

    private static string? GetImageReference(BackgroundProcessSnapshot snapshot)
        => snapshot.Metadata.TryGetValue(ImageReferenceMetadataKey, out var imageReference) ? imageReference : null;

    private void NotifySelectedImageCommands()
    {
        DeleteSelectedImageCommand.NotifyCanExecuteChanged();
        PullSelectedImageCommand.NotifyCanExecuteChanged();
        RefreshSelectedImageCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanUseSelectedImage));
        OnPropertyChanged(nameof(CanPullSelectedImage));
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _imageCatalogService.ImagesChanged -= OnImagesChanged;
        _backgroundProcessQueue.ProcessChanged -= BackgroundProcessQueue_OnProcessChanged;
    }

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }
}

public sealed partial class DockerImageRowViewModel : ObservableObject
{
    public DockerImageRowViewModel(DockerImageDefinition image)
    {
        ImageReference = image.ImageReference;
        _status = image.Status;
        _errorMessage = image.Status == DockerImageStatus.Failed ? image.LastMessage ?? string.Empty : string.Empty;
        DateText = image.LastPulledAtUtc is null
            ? string.Empty
            : $"Pulled {image.LastPulledAtUtc.Value.LocalDateTime:g}";
    }

    public string ImageReference { get; }

    public string DateText { get; }

    public bool HasDateText => !string.IsNullOrWhiteSpace(DateText);

    public string StatusText => Status switch
    {
        DockerImageStatus.Ready => "Ready",
        DockerImageStatus.Pulling => "Pulling",
        DockerImageStatus.Failed => "Failed",
        _ => "Not pulled",
    };

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DockerImageStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string _errorMessage;
}
