using Microsoft.Extensions.Logging;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed partial class DockerExecutionSettingsViewModel
{
    private ValueTask<DockerExecutionOperationResponse> InvokeRuntimeAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
        => _runtimeClient.IsAvailable
            ? _runtimeClient.InvokeAsync(DockerExecutionRuntimeOperations.Execute, request, cancellationToken)
            : ValueTask.FromException<DockerExecutionOperationResponse>(
                new PackageRuntimeInvocationException(
                    "runtime.v1.unavailable",
                    isTransient: true,
                    statusCode: 503));

    private async Task<DockerExecutionOperationResponse?> RunOperationAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken,
        bool showBusy,
        Func<bool>? hasAuthority = null)
    {
        long? busyOperation = null;
        if (showBusy)
        {
            await InvokePresentationAsync(() => busyOperation = BeginBusyOperation())
                .ConfigureAwait(false);
            if (busyOperation is null)
            {
                return null;
            }
        }

        try
        {
            var response = await InvokeRuntimeAsync(request, cancellationToken)
                .AsTask()
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var canApply = false;
            await InvokePresentationAsync(() =>
            {
                if (!HasOperationAuthority(busyOperation, hasAuthority))
                {
                    return;
                }

                canApply = true;
                if (response.Error is not null)
                {
                    ApplyDomainError(response.Error);
                }
            }).ConfigureAwait(false);
            if (!canApply || response.Error is not null)
            {
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
            await InvokePresentationAsync(() =>
            {
                if (HasOperationAuthority(busyOperation, hasAuthority))
                {
                    ApplyRuntimeFailure(exception);
                }
            }).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception)
        {
            await InvokePresentationAsync(() =>
            {
                if (!HasOperationAuthority(busyOperation, hasAuthority))
                {
                    return;
                }

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
            }).ConfigureAwait(false);
            return null;
        }
        finally
        {
            if (busyOperation is { } busyGeneration)
            {
                await InvokePresentationAsync(() => CompleteBusyOperation(busyGeneration))
                    .ConfigureAwait(false);
            }
        }
    }

    private bool HasOperationAuthority(long? busyOperation, Func<bool>? hasAuthority)
        => (busyOperation is null || IsBusyOperationCurrent(busyOperation.Value))
           && (hasAuthority is null || hasAuthority());

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

    private Task InvokePresentationAsync(Action action)
        => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });

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
}
