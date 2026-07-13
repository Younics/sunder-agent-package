using System.Collections.ObjectModel;
using Avalonia.Threading;
using Sunder.Package.Agent.Shared.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel : ObservableObject, IDisposable
{
    private readonly PresentationTaskScope _tasks = new();
    internal const string ImagePullGroupKey = "sunder.package.agent.execution.docker:image-pulls";
    internal const string ImageReferenceMetadataKey = "imageReference";

    private const string TimeoutKey = "docker.timeoutSeconds.default";
    private const string DefaultTimeoutSeconds = "300";

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [TimeoutKey, DockerCli.ExecutablePathConfigurationKey];

    private readonly DockerExecutionAppRuntimeClient _runtimeClient;
    private readonly IBackgroundProcessQueue _backgroundProcessQueue;
    private bool _disposed;

    internal DockerExecutionSettingsViewModel(
        DockerExecutionAppRuntimeClient runtimeClient,
        IBackgroundProcessQueue backgroundProcessQueue)
    {
        _runtimeClient = runtimeClient;
        _backgroundProcessQueue = backgroundProcessQueue;
        _backgroundProcessQueue.ProcessChanged += BackgroundProcessQueue_OnProcessChanged;
        TimeoutSeconds = DefaultTimeoutSeconds;
        DockerCliPath = string.Empty;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var response = await _runtimeClient.InvokeAsync(
            new DockerExecutionOperationRequest(DockerExecutionOperationKind.GetSettings),
            cancellationToken);
        TimeoutSeconds = response.TimeoutSeconds ?? DefaultTimeoutSeconds;
        DockerCliPath = response.DockerCliPath ?? string.Empty;
        ApplyImages(response.Images ?? []);
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

    private string _dockerCliPath = string.Empty;

    public string DockerCliPath
    {
        get => _dockerCliPath;
        private set => SetProperty(ref _dockerCliPath, value);
    }

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
        TestDockerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddImage));
        OnPropertyChanged(nameof(CanUseSelectedImage));
    }

    [RelayCommand(CanExecute = nameof(CanAddImage))]
    private async Task AddImageAsync()
    {
        try
        {
            var response = await _runtimeClient.InvokeAsync(new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.AddImage,
                ImageReference: NewImageReference));
            var selectedReference = NewImageReference.Trim();
            NewImageReference = string.Empty;
            ApplyImages(response.Images ?? [], selectedReference);
            StatusText = response.Message ?? "Docker image added.";
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
        var response = await _runtimeClient.InvokeAsync(new DockerExecutionOperationRequest(
            DockerExecutionOperationKind.DeleteImage,
            ImageReference: imageReference));
        ApplyImages(response.Images ?? []);
        StatusText = response.Message ?? $"Deleted Docker image '{imageReference}'.";
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
                        var response = await _runtimeClient.InvokeAsync(new DockerExecutionOperationRequest(
                            DockerExecutionOperationKind.PullImage,
                            ImageReference: imageReference), context.CancellationToken).ConfigureAwait(false);
                        if (!response.Success)
                        {
                            throw new InvalidOperationException(response.Message ?? "Docker image pull failed.");
                        }

                        context.ReportProgress(100, response.Message ?? "Docker image pull completed.");
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
            var response = await _runtimeClient.InvokeAsync(new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.RefreshImage,
                ImageReference: selected.ImageReference));
            ApplyImages(response.Images ?? [], selected.ImageReference);
            StatusText = response.Message ?? $"Refreshed Docker image '{selected.ImageReference}'.";
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
            var response = await _runtimeClient.InvokeAsync(new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.RefreshImages));
            ApplyImages(response.Images ?? [], SelectedImage?.ImageReference);
            StatusText = response.Message ?? "Docker image status refreshed.";
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
    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        if (!BoundedValue.TryParseInt32(
                TimeoutSeconds,
                minimum: 1,
                maximum: BoundedProcessRunner.MaximumTimeoutSeconds,
                out var timeoutSeconds))
        {
            StatusText = $"Docker command timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.";
            return;
        }

        IsBusy = true;
        try
        {
            var dockerCliPath = DockerCliPath.Trim();
            var response = await _runtimeClient.InvokeAsync(new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.SaveSettings,
                TimeoutSeconds: timeoutSeconds.ToString(),
                DockerCliPath: dockerCliPath), cancellationToken);
            TimeoutSeconds = response.TimeoutSeconds ?? timeoutSeconds.ToString();
            DockerCliPath = response.DockerCliPath ?? dockerCliPath;
            StatusText = response.Message ?? "Docker execution settings saved.";
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
    private async Task TestDockerAsync(CancellationToken cancellationToken)
    {
        IsBusy = true;
        try
        {
            var response = await _runtimeClient.InvokeAsync(
                new DockerExecutionOperationRequest(DockerExecutionOperationKind.TestDocker),
                cancellationToken);
            StatusText = response.Message ?? (response.Success ? "Docker is available." : "Docker is unavailable.");
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

    [RelayCommand]
    private void ClearDockerCliPath() => DockerCliPath = string.Empty;

    internal bool ApplySelectedDockerCliPath(string path)
    {
        if (path.Length is 0 or > 1024 || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        DockerCliPath = path;
        return true;
    }

    private bool CanRunBusyCommand() => !IsBusy;

    private async Task ReloadImagesAsync(
        string? selectedImageReference = null,
        CancellationToken cancellationToken = default)
    {
        var response = await _runtimeClient.InvokeAsync(
            new DockerExecutionOperationRequest(DockerExecutionOperationKind.GetSettings),
            cancellationToken);
        ApplyImages(response.Images ?? [], selectedImageReference);
    }

    private void ApplyImages(
        IReadOnlyList<DockerImageDefinition> images,
        string? selectedImageReference = null)
    {
        var activePulls = GetActivePullImageReferences();
        Images.Clear();
        foreach (var image in images)
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
        _tasks.Run(async cancellationToken =>
        {
            Task reload = Task.CompletedTask;
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    reload = ReloadImagesAsync(SelectedImage?.ImageReference ?? imageReference);
                }
            }, DispatcherPriority.Background);
            await reload.WaitAsync(cancellationToken);
        });
    }

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
        _backgroundProcessQueue.ProcessChanged -= BackgroundProcessQueue_OnProcessChanged;
        _tasks.Dispose();
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
