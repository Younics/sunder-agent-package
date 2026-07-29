using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel
{
    [RelayCommand(CanExecute = nameof(IsError))]
    private async Task RetryInitializationAsync()
        => await InitializeAsync();

    [RelayCommand(CanExecute = nameof(CanAddImage))]
    private async Task AddImageAsync()
    {
        if (!CanAddImage)
        {
            return;
        }
        var imageReference = NewImageReference.Trim();
        var fieldRevision = _newImageReferenceRevision;
        var selectionRevision = _selectionRevision;
        var request = _requests.Begin(ImageListChannel, _lifetime.Token);
        try
        {
            var response = await RunOperationAsync(new DockerExecutionOperationRequest(
                    DockerExecutionOperationKind.AddImage,
                    ImageReference: imageReference),
                request.CancellationToken,
                showBusy: true,
                () => _requests.IsCurrent(request));
            if (response is null)
            {
                return;
            }
            if (!TryGetImageSnapshot(response, "AddImage", out var images, out var catalogRevision))
            {
                return;
            }
            if (fieldRevision == _newImageReferenceRevision)
            {
                NewImageReference = string.Empty;
            }
            ApplyImages(
                images,
                imageReference,
                selectionRevision);
            _catalogRevision = catalogRevision;
            ClearRuntimeError();
            StatusText = response.Message ?? "Docker image added.";
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedImage))]
    private async Task DeleteSelectedImageAsync()
    {
        if (!CanUseSelectedImage || SelectedImage is null)
        {
            return;
        }

        var imageReference = SelectedImage.ImageReference;
        var selectionRevision = _selectionRevision;
        var request = _requests.Begin(ImageListChannel, _lifetime.Token);
        try
        {
            var response = await RunOperationAsync(new DockerExecutionOperationRequest(
                    DockerExecutionOperationKind.DeleteImage,
                    ImageReference: imageReference),
                request.CancellationToken,
                showBusy: true,
                () => _requests.IsCurrent(request));
            if (response is null
                || !TryGetImageSnapshot(response, "DeleteImage", out var images, out var catalogRevision))
            {
                return;
            }
            ApplyImages(images, expectedSelectionRevision: selectionRevision);
            _catalogRevision = catalogRevision;
            ClearRuntimeError();
            StatusText = response.Message ?? $"Deleted Docker image '{imageReference}'.";
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    [RelayCommand(CanExecute = nameof(CanPullSelectedImage))]
    private void PullSelectedImage()
    {
        if (!CanPullSelectedImage || SelectedImage is null)
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
                        if (response.Error is not null)
                        {
                            throw new InvalidOperationException(FormatOperationError(response.Error));
                        }
                        if (!response.Success)
                        {
                            throw new InvalidOperationException("Docker image pull failed.");
                        }

                        context.ReportProgress(100, response.Message ?? "Docker image pull completed.");
                    },
                    new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                    {
                        [ImageReferenceMetadataKey] = imageReference,
                    }));
            }

            selected.MarkPulling();
            NotifySelectedImageCommands();
            _tasks.Run(_imageRefresh.MarkDirty());
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            _logger?.LogError(
                ex,
                "Docker image pull could not be queued. CorrelationId: {CorrelationId}",
                correlationId);
            _tasks.Run(_imageRefresh.MarkDirty());
            RuntimeErrorCode = "docker.presentation.pull-queue-failed";
            RuntimeCorrelationId = correlationId;
            StatusText = FormatError(
                "Docker image pull could not be queued.",
                "docker.presentation.pull-queue-failed",
                correlationId);
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelectedImage))]
    private async Task RefreshSelectedImageAsync()
    {
        if (!CanUseSelectedImage || SelectedImage is null)
        {
            return;
        }

        var selected = SelectedImage;
        var selectionRevision = _selectionRevision;
        var request = _requests.Begin(ImageListChannel, _lifetime.Token);
        try
        {
            var response = await RunOperationAsync(new DockerExecutionOperationRequest(
                    DockerExecutionOperationKind.RefreshImage,
                    ImageReference: selected.ImageReference),
                request.CancellationToken,
                showBusy: true,
                () => _requests.IsCurrent(request));
            if (response is not null
                && TryGetImageSnapshot(response, "RefreshImage", out var images, out var catalogRevision))
            {
                ApplyImages(
                    images,
                    selected.ImageReference,
                    selectionRevision);
                _catalogRevision = catalogRevision;
                ClearRuntimeError();
                StatusText = response.Message ?? $"Refreshed Docker image '{selected.ImageReference}'.";
            }
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBusyCommand))]
    private async Task RefreshAllImagesAsync()
    {
        if (!CanMutate)
        {
            return;
        }
        var selectionRevision = _selectionRevision;
        var selectedReference = SelectedImage?.ImageReference;
        var request = _requests.Begin(ImageListChannel, _lifetime.Token);
        try
        {
            var response = await RunOperationAsync(
                new DockerExecutionOperationRequest(DockerExecutionOperationKind.RefreshImages),
                request.CancellationToken,
                showBusy: true,
                () => _requests.IsCurrent(request));
            if (response is not null
                && TryGetImageSnapshot(response, "RefreshImages", out var images, out var catalogRevision))
            {
                ApplyImages(images, selectedReference, selectionRevision);
                _catalogRevision = catalogRevision;
                ClearRuntimeError();
                StatusText = response.Message ?? "Docker image status refreshed.";
            }
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    [RelayCommand(CanExecute = nameof(CanRunBusyCommand))]
    private async Task SaveSettingsAsync(CancellationToken cancellationToken)
    {
        if (!CanMutate)
        {
            return;
        }
        if (!BoundedValue.TryParseInt32(
                TimeoutSeconds,
                minimum: 1,
                maximum: BoundedProcessRunner.MaximumTimeoutSeconds,
                out var timeoutSeconds))
        {
            StatusText = $"Docker command timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.";
            return;
        }

        var timeoutRevision = _timeoutRevision;
        var pathRevision = _dockerCliPathRevision;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var dockerCliPath = DockerCliPath.Trim();
        var response = await RunOperationAsync(new DockerExecutionOperationRequest(
                DockerExecutionOperationKind.SaveSettings,
                TimeoutSeconds: timeoutSeconds.ToString(),
                DockerCliPath: dockerCliPath),
            linkedCancellation.Token,
            showBusy: true);
        if (response is null)
        {
            return;
        }
        if (response.TimeoutSeconds is null || response.DockerCliPath is null)
        {
            SetProtocolError("SaveSettings response omitted required fields.");
            return;
        }

        ApplyTimeoutSeconds(response.TimeoutSeconds, timeoutRevision);
        ApplyDockerCliPath(response.DockerCliPath, pathRevision);
        AdvanceSettingsAuthority();
        ClearRuntimeError();
        StatusText = response.Message ?? "Docker execution settings saved.";
    }

    [RelayCommand(CanExecute = nameof(CanRunBusyCommand))]
    private async Task TestDockerAsync(CancellationToken cancellationToken)
    {
        if (!CanMutate)
        {
            return;
        }
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var response = await RunOperationAsync(
            new DockerExecutionOperationRequest(DockerExecutionOperationKind.TestDocker),
            linkedCancellation.Token,
            showBusy: true);
        if (response is not null)
        {
            ClearRuntimeError();
            StatusText = response.Message
                ?? (response.Success ? "Docker is available." : "Docker is unavailable.");
        }
    }

    [RelayCommand]
    private void ClearDockerCliPath()
    {
        if (CanMutate)
        {
            DockerCliPath = string.Empty;
        }
    }

    internal bool ApplySelectedDockerCliPath(string path)
    {
        if (!IsReady || _disposed || path.Length is 0 or > 1024 || !Path.IsPathFullyQualified(path))
        {
            return false;
        }

        DockerCliPath = path;
        return true;
    }
}
