using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Shared.Presentation;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private readonly PresentationTaskScope _tasks = new();
    private readonly LatestRequestCoordinator _requests = new();
    private readonly SerializedRefreshLoop _imageRefresh;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _initializationSyncRoot = new();
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly ILogger? _logger;
    private const string ImageListChannel = "docker-images";
    private const string FullSettingsChannel = "docker-settings";
    internal const string ImagePullGroupKey = "sunder.package.agent.execution.docker:image-pulls";
    internal const string ImageReferenceMetadataKey = "imageReference";

    private const string TimeoutKey = "docker.timeoutSeconds.default";
    private const string DefaultTimeoutSeconds = "300";

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [TimeoutKey, DockerCli.ExecutablePathConfigurationKey];

    private readonly IPackageRuntimeClient _runtimeClient;
    private readonly IBackgroundProcessQueue _backgroundProcessQueue;
    private bool _suppressRevisionTracking;
    private bool _disposed;
    private long _busyGeneration;
    private long _selectionRevision;
    private long _newImageReferenceRevision;
    private long _timeoutRevision;
    private long _dockerCliPathRevision;
    private long _settingsRevision;
    private long _catalogRevision;
    private Task? _initialization;

    internal DockerExecutionSettingsViewModel(
        IPackageRuntimeClient runtimeClient,
        IBackgroundProcessQueue backgroundProcessQueue,
        ILogger? logger = null,
        IPresentationDispatcher? uiDispatcher = null)
    {
        _runtimeClient = runtimeClient;
        _backgroundProcessQueue = backgroundProcessQueue;
        _logger = logger;
        _uiDispatcher = uiDispatcher ?? PresentationDispatcher.Capture();
        _imageRefresh = new SerializedRefreshLoop(
            cancellationToken => ReloadImagesAsync(cancellationToken: cancellationToken),
            ReportImageRefreshFailure);
        _backgroundProcessQueue.ProcessChanged += BackgroundProcessQueue_OnProcessChanged;
        TimeoutSeconds = DefaultTimeoutSeconds;
        DockerCliPath = string.Empty;
    }

    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        Task initialization;
        lock (_initializationSyncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_initialization is null
                || LoadState == DockerSettingsLoadState.Error && _initialization.IsCompleted)
            {
                _initialization = InitializeCoreAsync();
            }
            initialization = _initialization;
        }

        return cancellationToken.CanBeCanceled
            ? initialization.WaitAsync(cancellationToken)
            : initialization;
    }

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        if (LoadState is DockerSettingsLoadState.Uninitialized or DockerSettingsLoadState.Error)
        {
            await InitializeAsync(cancellationToken);
        }
        else if (LoadState == DockerSettingsLoadState.Ready)
        {
            await LoadSettingsSnapshotAsync(cancellationToken);
        }
        else
        {
            await InitializeAsync(cancellationToken);
        }
        return true;
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ObservableCollection<DockerImageRowViewModel> Images { get; } = [];

    public bool HasImages => Images.Count > 0;

    public bool IsLoading => LoadState == DockerSettingsLoadState.Loading;

    public bool IsReady => LoadState == DockerSettingsLoadState.Ready;

    public bool IsError => LoadState == DockerSettingsLoadState.Error;

    public bool CanMutate => IsReady && !IsBusy && !_disposed;

    public bool HasRuntimeError => !string.IsNullOrWhiteSpace(RuntimeErrorCode);

    public bool CanAddImage => CanMutate && !string.IsNullOrWhiteSpace(NewImageReference);

    public bool CanUseSelectedImage => CanMutate && SelectedImage is { Status: not DockerImageStatus.Pulling };

    public bool CanPullSelectedImage => CanUseSelectedImage
        && SelectedImage?.Status != DockerImageStatus.NeedsAttention;

    [ObservableProperty]
    private DockerImageRowViewModel? _selectedImage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoading))]
    [NotifyPropertyChangedFor(nameof(IsReady))]
    [NotifyPropertyChangedFor(nameof(IsError))]
    [NotifyPropertyChangedFor(nameof(CanMutate))]
    private DockerSettingsLoadState _loadState;

    [ObservableProperty]
    private string _newImageReference = string.Empty;

    [ObservableProperty]
    private string _timeoutSeconds;

    private string _dockerCliPath = string.Empty;

    public string DockerCliPath
    {
        get => _dockerCliPath;
        private set
        {
            if (SetProperty(ref _dockerCliPath, value) && !_suppressRevisionTracking)
            {
                _dockerCliPathRevision++;
                _settingsRevision++;
            }
        }
    }

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string? _runtimeErrorCode;

    [ObservableProperty]
    private string? _runtimeCorrelationId;

    partial void OnSelectedImageChanged(DockerImageRowViewModel? value)
    {
        if (!_suppressRevisionTracking)
        {
            _selectionRevision++;
        }
        NotifySelectedImageCommands();
    }

    partial void OnNewImageReferenceChanged(string value)
    {
        if (!_suppressRevisionTracking)
        {
            _newImageReferenceRevision++;
        }
        AddImageCommand.NotifyCanExecuteChanged();
    }

    partial void OnTimeoutSecondsChanged(string value)
    {
        if (!_suppressRevisionTracking)
        {
            _timeoutRevision++;
            _settingsRevision++;
        }
    }

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

    partial void OnLoadStateChanged(DockerSettingsLoadState value)
    {
        RetryInitializationCommand.NotifyCanExecuteChanged();
        AddImageCommand.NotifyCanExecuteChanged();
        NotifySelectedImageCommands();
        RefreshAllImagesCommand.NotifyCanExecuteChanged();
        SaveSettingsCommand.NotifyCanExecuteChanged();
        TestDockerCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanAddImage));
        OnPropertyChanged(nameof(CanUseSelectedImage));
        OnPropertyChanged(nameof(CanPullSelectedImage));
    }

    partial void OnRuntimeErrorCodeChanged(string? value)
        => OnPropertyChanged(nameof(HasRuntimeError));

    private async Task InitializeCoreAsync()
    {
        await InvokePresentationAsync(() => SetLoadState(DockerSettingsLoadState.Loading))
            .ConfigureAwait(false);
        await LoadSettingsSnapshotAsync(_lifetime.Token).ConfigureAwait(false);
    }

    private async Task LoadSettingsSnapshotAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var settingsRequest = _requests.Begin(FullSettingsChannel, linkedCancellation.Token);
        var imageRequest = _requests.Begin(ImageListChannel, linkedCancellation.Token);
        var selectionRevision = 0L;
        var timeoutRevision = 0L;
        var pathRevision = 0L;
        var settingsRevision = 0L;
        var snapshotCaptured = false;
        try
        {
            await InvokePresentationAsync(() =>
            {
                selectionRevision = _selectionRevision;
                timeoutRevision = _timeoutRevision;
                pathRevision = _dockerCliPathRevision;
                settingsRevision = _settingsRevision;
                snapshotCaptured = true;
            }).ConfigureAwait(false);
            if (!snapshotCaptured)
            {
                return;
            }

            var response = await RunOperationAsync(
                new DockerExecutionOperationRequest(DockerExecutionOperationKind.GetSettings),
                settingsRequest.CancellationToken,
                showBusy: false,
                () => _requests.IsCurrent(settingsRequest)).ConfigureAwait(false);
            if (response is null)
            {
                await InvokePresentationAsync(() =>
                {
                    if (!linkedCancellation.IsCancellationRequested
                        && _requests.IsCurrent(settingsRequest))
                    {
                        SetLoadState(DockerSettingsLoadState.Error);
                    }
                }).ConfigureAwait(false);
                return;
            }

            await InvokePresentationAsync(() =>
            {
                if (!_requests.IsCurrent(settingsRequest))
                {
                    return;
                }
                if (response.TimeoutSeconds is null || response.DockerCliPath is null)
                {
                    SetProtocolError("GetSettings response omitted settings fields.");
                    SetLoadState(DockerSettingsLoadState.Error);
                    return;
                }
                if (!TryGetImageSnapshot(response, "GetSettings", out var images, out var catalogRevision))
                {
                    SetLoadState(DockerSettingsLoadState.Error);
                    return;
                }

                if (settingsRevision == _settingsRevision)
                {
                    ApplyTimeoutSeconds(response.TimeoutSeconds, timeoutRevision);
                    ApplyDockerCliPath(response.DockerCliPath, pathRevision);
                }
                if (_requests.IsCurrent(imageRequest))
                {
                    ApplyImages(images, expectedSelectionRevision: selectionRevision);
                    _catalogRevision = catalogRevision;
                }
                ClearRuntimeError();
                if (LoadState == DockerSettingsLoadState.Loading)
                {
                    StatusText = string.Empty;
                }
                SetLoadState(DockerSettingsLoadState.Ready);
            }).ConfigureAwait(false);
        }
        finally
        {
            _requests.Complete(settingsRequest);
            _requests.Complete(imageRequest);
        }
    }

    private void ApplyTimeoutSeconds(string value, long expectedRevision)
    {
        if (_disposed || expectedRevision != _timeoutRevision)
        {
            return;
        }

        _suppressRevisionTracking = true;
        try
        {
            TimeoutSeconds = value;
        }
        finally
        {
            _suppressRevisionTracking = false;
        }
    }

    private void ApplyDockerCliPath(string value, long expectedRevision)
    {
        if (_disposed || expectedRevision != _dockerCliPathRevision)
        {
            return;
        }

        _suppressRevisionTracking = true;
        try
        {
            DockerCliPath = value;
        }
        finally
        {
            _suppressRevisionTracking = false;
        }
    }

    private void AdvanceSettingsAuthority()
    {
        _settingsRevision++;
        _timeoutRevision++;
        _dockerCliPathRevision++;
        _requests.Invalidate(FullSettingsChannel);
    }

    private bool CanRunBusyCommand() => CanMutate;

    private async Task ReloadImagesAsync(
        string? selectedImageReference = null,
        CancellationToken cancellationToken = default)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var selectionRevision = 0L;
        var snapshotCaptured = false;
        await InvokePresentationAsync(() =>
        {
            selectionRevision = _selectionRevision;
            selectedImageReference ??= SelectedImage?.ImageReference;
            snapshotCaptured = true;
        }).ConfigureAwait(false);
        if (!snapshotCaptured)
        {
            return;
        }

        var request = _requests.Begin(ImageListChannel, linkedCancellation.Token);
        try
        {
            var response = await RunOperationAsync(
                new DockerExecutionOperationRequest(DockerExecutionOperationKind.GetSettings),
                request.CancellationToken,
                showBusy: false,
                () => _requests.IsCurrent(request)).ConfigureAwait(false);
            if (response is null)
            {
                return;
            }

            await InvokePresentationAsync(() =>
            {
                if (!_requests.IsCurrent(request)
                    || !TryGetImageSnapshot(response, "GetSettings", out var images, out var catalogRevision))
                {
                    return;
                }

                ApplyImages(
                    images,
                    selectedImageReference,
                    selectionRevision);
                _catalogRevision = catalogRevision;
            }).ConfigureAwait(false);
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    private void ApplyImages(
        IReadOnlyList<DockerImageDefinition> images,
        string? selectedImageReference = null,
        long? expectedSelectionRevision = null)
    {
        if (_disposed)
        {
            return;
        }

        var currentSelectionReference = SelectedImage?.ImageReference;
        var activePulls = GetActivePullImageReferences();
        var desired = images.Select(image =>
        {
            var displayedImage = activePulls.Contains(image.ImageReference)
                ? image with { Status = DockerImageStatus.Pulling, LastMessage = "Pulling image..." }
                : image;
            return displayedImage;
        }).ToArray();
        var desiredReferences = desired.Select(image => image.ImageReference)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = Images.Count - 1; index >= 0; index--)
        {
            if (!desiredReferences.Contains(Images[index].ImageReference))
            {
                Images.RemoveAt(index);
            }
        }
        for (var index = 0; index < desired.Length; index++)
        {
            var image = desired[index];
            var existing = Images.FirstOrDefault(row => string.Equals(
                row.ImageReference,
                image.ImageReference,
                StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                Images.Insert(index, new DockerImageRowViewModel(image));
            }
            else
            {
                existing.Apply(image);
                var existingIndex = Images.IndexOf(existing);
                if (existingIndex != index)
                {
                    Images.Move(existingIndex, index);
                }
            }
        }

        var selectionReference = expectedSelectionRevision is not null
                                 && expectedSelectionRevision != _selectionRevision
            ? currentSelectionReference
            : selectedImageReference;
        _suppressRevisionTracking = true;
        try
        {
            SelectedImage = !string.IsNullOrWhiteSpace(selectionReference)
                ? Images.FirstOrDefault(image => string.Equals(
                      image.ImageReference,
                      selectionReference,
                      StringComparison.OrdinalIgnoreCase)) ?? Images.FirstOrDefault()
                : Images.FirstOrDefault();
        }
        finally
        {
            _suppressRevisionTracking = false;
        }
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

        _tasks.Run(_imageRefresh.MarkDirty());
    }

    private void ReportImageRefreshFailure(Exception exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger?.LogError(
            exception,
            "Docker image background refresh failed. CorrelationId: {CorrelationId}",
            correlationId);
        _tasks.Run(InvokePresentationAsync(() =>
        {
            RuntimeErrorCode = "docker.presentation.refresh-failed";
            RuntimeCorrelationId = correlationId;
            StatusText = FormatError(
                "Docker image refresh failed.",
                "docker.presentation.refresh-failed",
                correlationId);
        }));
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
        _busyGeneration++;
        _lifetime.Cancel();
        _backgroundProcessQueue.ProcessChanged -= BackgroundProcessQueue_OnProcessChanged;
        _imageRefresh.Dispose();
        _requests.Dispose();
        _tasks.Dispose();
        _lifetime.Dispose();
    }

}

public enum DockerSettingsLoadState
{
    Uninitialized = 0,
    Loading = 1,
    Ready = 2,
    Error = 3,
}

public sealed partial class DockerImageRowViewModel : ObservableObject
{
    public DockerImageRowViewModel(DockerImageDefinition image)
    {
        ImageReference = image.ImageReference;
        Apply(image);
    }

    public string ImageReference { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDateText))]
    private string _dateText = string.Empty;

    public bool HasDateText => !string.IsNullOrWhiteSpace(DateText);

    public string StatusText => Status switch
    {
        DockerImageStatus.Ready => "Ready",
        DockerImageStatus.Pulling => "Pulling",
        DockerImageStatus.Failed => "Failed",
        DockerImageStatus.NeedsAttention => "Needs attention",
        _ => "Not pulled",
    };

    public bool HasErrorMessage => !string.IsNullOrWhiteSpace(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText))]
    private DockerImageStatus _status;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasErrorMessage))]
    private string _errorMessage = string.Empty;

    internal void Apply(DockerImageDefinition image)
    {
        if (!string.Equals(ImageReference, image.ImageReference, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot reconcile Docker image rows with different references.");
        }

        Status = image.Status;
        ErrorMessage = image.Status is DockerImageStatus.Failed or DockerImageStatus.NeedsAttention
            ? image.LastMessage ?? string.Empty
            : string.Empty;
        DateText = image.LastPulledAtUtc is null
            ? string.Empty
            : $"Pulled {image.LastPulledAtUtc.Value.LocalDateTime:g}";
    }

    internal void MarkPulling()
    {
        Status = DockerImageStatus.Pulling;
        ErrorMessage = string.Empty;
    }
}
