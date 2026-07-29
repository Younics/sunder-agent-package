using System.Collections.ObjectModel;
using Avalonia.Threading;
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
    private readonly ILogger? _logger;
    private const string ImageListChannel = "docker-images";
    private const string FullSettingsChannel = "docker-settings";
    internal const string ImagePullGroupKey = "sunder.package.agent.execution.docker:image-pulls";
    internal const string ImageReferenceMetadataKey = "imageReference";

    private const string TimeoutKey = "docker.timeoutSeconds.default";
    private const string DefaultTimeoutSeconds = "300";

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } =
        [TimeoutKey, DockerCli.ExecutablePathConfigurationKey];

    private readonly DockerExecutionAppRuntimeClient _runtimeClient;
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
        DockerExecutionAppRuntimeClient runtimeClient,
        IBackgroundProcessQueue backgroundProcessQueue,
        ILogger? logger = null)
    {
        _runtimeClient = runtimeClient;
        _backgroundProcessQueue = backgroundProcessQueue;
        _logger = logger;
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
        SetLoadState(DockerSettingsLoadState.Loading);
        await LoadSettingsSnapshotAsync(_lifetime.Token);
    }

    private async Task LoadSettingsSnapshotAsync(CancellationToken cancellationToken)
    {
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var settingsRequest = _requests.Begin(FullSettingsChannel, linkedCancellation.Token);
        var imageRequest = _requests.Begin(ImageListChannel, linkedCancellation.Token);
        var selectionRevision = _selectionRevision;
        var timeoutRevision = _timeoutRevision;
        var pathRevision = _dockerCliPathRevision;
        var settingsRevision = _settingsRevision;
        try
        {
            var response = await RunOperationAsync(
                new DockerExecutionOperationRequest(DockerExecutionOperationKind.GetSettings),
                settingsRequest.CancellationToken,
                showBusy: false,
                () => _requests.IsCurrent(settingsRequest));
            if (response is null)
            {
                if (!_disposed
                    && !linkedCancellation.IsCancellationRequested
                    && _requests.IsCurrent(settingsRequest))
                {
                    SetLoadState(DockerSettingsLoadState.Error);
                }
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

            if (_requests.IsCurrent(settingsRequest) && settingsRevision == _settingsRevision)
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
        }
        finally
        {
            _requests.Complete(settingsRequest);
            _requests.Complete(imageRequest);
        }
    }

    private async Task<DockerExecutionOperationResponse?> RunOperationAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken,
        bool showBusy,
        Func<bool>? hasAuthority = null)
    {
        var busyOperation = showBusy ? BeginBusyOperation() : (long?)null;
        try
        {
            var response = await _runtimeClient.InvokeAsync(request, cancellationToken)
                .AsTask()
                .WaitAsync(cancellationToken);
            if (_disposed
                || busyOperation is { } activeGeneration && !IsBusyOperationCurrent(activeGeneration)
                || hasAuthority is not null && !hasAuthority())
            {
                return null;
            }
            if (response.Error is not null)
            {
                ApplyDomainError(response.Error);
                return null;
            }
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (PackageRuntimeInvocationException exception)
        {
            if (hasAuthority is null || hasAuthority())
            {
                ApplyRuntimeFailure(exception);
            }
            return null;
        }
        catch (Exception exception)
        {
            if (!_disposed && (hasAuthority is null || hasAuthority()))
            {
                var correlationId = Guid.NewGuid().ToString("N");
                _logger?.LogError(
                    exception,
                    "Docker settings operation failed. CorrelationId: {CorrelationId}; Operation: {Operation}",
                    correlationId,
                    request.Kind);
                RuntimeErrorCode = "docker.presentation.failed";
                RuntimeCorrelationId = correlationId;
                StatusText = FormatError(
                    "Docker settings operation failed.",
                    "docker.presentation.failed",
                    correlationId);
            }
            return null;
        }
        finally
        {
            if (busyOperation is { } busyGeneration)
            {
                CompleteBusyOperation(busyGeneration);
            }
        }
    }

    private bool TryGetImageSnapshot(
        DockerExecutionOperationResponse response,
        string operation,
        out IReadOnlyList<DockerImageDefinition> images,
        out long catalogRevision)
    {
        if (response.Images is null
            || response.CatalogRevision is not { } revision
            || revision < 0)
        {
            SetProtocolError($"{operation} response omitted image snapshot fields.");
            images = [];
            catalogRevision = default;
            return false;
        }
        if (revision < _catalogRevision)
        {
            SetProtocolError($"{operation} response returned a regressive catalog revision.");
            images = [];
            catalogRevision = default;
            return false;
        }
        images = response.Images;
        catalogRevision = revision;
        return true;
    }

    private void ApplyDomainError(DockerExecutionOperationError error)
    {
        if (!IsSafeLowercaseToken(error.Code)
            || string.IsNullOrWhiteSpace(error.Message)
            || error.Message.Length > 1024
            || !IsSafeToken(error.CorrelationId))
        {
            SetProtocolError("Operation error payload is invalid.");
            return;
        }
        RuntimeErrorCode = error.Code;
        RuntimeCorrelationId = error.CorrelationId;
        StatusText = FormatOperationError(error);
    }

    private void ApplyRuntimeFailure(PackageRuntimeInvocationException exception)
    {
        RuntimeErrorCode = exception.Code;
        RuntimeCorrelationId = exception.CorrelationId;
        StatusText = FormatError(
            "Docker Runtime request failed.",
            exception.Code,
            exception.CorrelationId);
        _logger?.LogWarning(
            "Docker Runtime request failed. Code: {Code}; CorrelationId: {CorrelationId}",
            exception.Code,
            exception.CorrelationId);
    }

    private void SetProtocolError(string diagnostic)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        RuntimeErrorCode = "docker.protocol.invalid-response";
        RuntimeCorrelationId = correlationId;
        StatusText = FormatError(
            "Docker Runtime returned an invalid response.",
            "docker.protocol.invalid-response",
            correlationId);
        _logger?.LogError(
            "Docker Runtime protocol failure. CorrelationId: {CorrelationId}; Diagnostic: {Diagnostic}",
            correlationId,
            diagnostic);
    }

    private void ClearRuntimeError()
    {
        RuntimeErrorCode = null;
        RuntimeCorrelationId = null;
    }

    private void SetLoadState(DockerSettingsLoadState state)
    {
        if (!_disposed)
        {
            LoadState = state;
        }
    }

    private static string FormatOperationError(DockerExecutionOperationError error)
        => FormatError(error.Message, error.Code, error.CorrelationId);

    private static string FormatError(string message, string code, string? correlationId)
        => string.IsNullOrWhiteSpace(correlationId)
            ? $"{message} (code: {code})"
            : $"{message} (code: {code}; correlation: {correlationId})";

    private static bool IsSafeLowercaseToken(string? value)
        => IsSafeToken(value)
           && value!.All(character => !char.IsAsciiLetter(character)
                                      || char.IsAsciiLetterLower(character));

    private static bool IsSafeToken(string? value)
        => value is { Length: > 0 and <= 128 }
           && value.All(character => char.IsAsciiLetterOrDigit(character)
                                     || character is '.' or '-' or '_');

    private long BeginBusyOperation()
    {
        var generation = ++_busyGeneration;
        if (!_disposed)
        {
            IsBusy = true;
        }
        return generation;
    }

    private bool IsBusyOperationCurrent(long generation)
        => !_disposed && generation == _busyGeneration;

    private void CompleteBusyOperation(long generation)
    {
        if (IsBusyOperationCurrent(generation))
        {
            IsBusy = false;
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
        var request = _requests.Begin(ImageListChannel, linkedCancellation.Token);
        var selectionRevision = _selectionRevision;
        selectedImageReference ??= SelectedImage?.ImageReference;
        try
        {
            var response = await RunOperationAsync(
                new DockerExecutionOperationRequest(DockerExecutionOperationKind.GetSettings),
                request.CancellationToken,
                showBusy: false,
                () => _requests.IsCurrent(request));
            if (response is null
                || !TryGetImageSnapshot(response, "GetSettings", out var images, out var catalogRevision))
            {
                return;
            }
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed && _requests.IsCurrent(request))
                {
                    ApplyImages(
                        images,
                        selectedImageReference,
                        selectionRevision);
                    _catalogRevision = catalogRevision;
                }
            }, DispatcherPriority.Background);
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
        _tasks.Run(async cancellationToken =>
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    RuntimeErrorCode = "docker.presentation.refresh-failed";
                    RuntimeCorrelationId = correlationId;
                    StatusText = FormatError(
                        "Docker image refresh failed.",
                        "docker.presentation.refresh-failed",
                        correlationId);
                }
            }, DispatcherPriority.Background);
            cancellationToken.ThrowIfCancellationRequested();
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
