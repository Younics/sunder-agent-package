using Microsoft.Extensions.Logging;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Execution.Docker;

internal sealed class DockerExecutionRuntimeOperationHandler(
    IPackageContext packageContext,
    DockerCliRunner dockerCliRunner,
    DockerImageCatalogService imageCatalog,
    DockerExecutionWorkspaceEditorContributor workspaceEditor)
    : IPackageRuntimeOperationHandler<DockerExecutionOperationRequest, DockerExecutionOperationResponse>
{
    private const int MaximumPathLength = 1024;
    private readonly ILogger _logger = packageContext.Logging.LoggerFactory
        .CreateLogger<DockerExecutionRuntimeOperationHandler>();

    public async ValueTask<DockerExecutionOperationResponse> HandleAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return request.Kind switch
            {
                DockerExecutionOperationKind.GetSettings => await GetSettingsAsync(cancellationToken),
                DockerExecutionOperationKind.SaveSettings => await SaveSettingsAsync(request, cancellationToken),
                DockerExecutionOperationKind.AddImage => await AddImageAsync(request, cancellationToken),
                DockerExecutionOperationKind.DeleteImage => await DeleteImageAsync(request, cancellationToken),
                DockerExecutionOperationKind.RefreshImage => await RefreshImageAsync(request, cancellationToken),
                DockerExecutionOperationKind.RefreshImages => await RefreshImagesAsync(cancellationToken),
                DockerExecutionOperationKind.PullImage => await PullImageAsync(request, cancellationToken),
                DockerExecutionOperationKind.TestDocker => await TestDockerAsync(cancellationToken),
                DockerExecutionOperationKind.GetWorkspaceEditor => await GetWorkspaceEditorAsync(request, cancellationToken),
                DockerExecutionOperationKind.SaveWorkspaceEditor => await SaveWorkspaceEditorAsync(request, cancellationToken),
                _ => throw new DockerExecutionDomainException(
                    "docker.operation.unknown",
                    "The requested Docker execution operation is not supported."),
            };
        }
        catch (DockerExecutionDomainException exception)
        {
            return CreateDomainFailure(request.Kind, exception);
        }
    }

    private async ValueTask<DockerExecutionOperationResponse> GetSettingsAsync(
        CancellationToken cancellationToken)
    {
        var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
        return new DockerExecutionOperationResponse(
            TimeoutSeconds: await packageContext.Settings.GetValueAsync(
                    DockerExecutionConfiguration.TimeoutKey,
                    cancellationToken)
                ?? DockerExecutionConfiguration.DefaultTimeoutSeconds,
            DockerCliPath: await packageContext.Settings.GetValueAsync(
                    DockerCli.ExecutablePathConfigurationKey,
                    cancellationToken)
                ?? string.Empty,
            Images: snapshot.Images,
            CatalogRevision: snapshot.Revision);
    }

    private async ValueTask<DockerExecutionOperationResponse> SaveSettingsAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        if (!BoundedValue.TryParseInt32(
                request.TimeoutSeconds,
                minimum: 1,
                maximum: BoundedProcessRunner.MaximumTimeoutSeconds,
                out var timeoutSeconds))
        {
            throw new DockerExecutionDomainException(
                "docker.timeout.invalid",
                $"Docker command timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.");
        }

        var dockerCliPath = request.DockerCliPath?.Trim() ?? string.Empty;
        if (dockerCliPath.Length > 0
            && (dockerCliPath.Length > MaximumPathLength
                || !Path.IsPathFullyQualified(dockerCliPath)
                || !File.Exists(dockerCliPath)))
        {
            throw new DockerExecutionDomainException(
                "docker.cli-path.invalid",
                "Docker CLI path must be a bounded absolute executable path selected from the local filesystem.");
        }

        await packageContext.Settings.SetValueAsync(
            DockerExecutionConfiguration.TimeoutKey,
            timeoutSeconds.ToString(),
            cancellationToken);
        if (dockerCliPath.Length == 0)
        {
            await packageContext.Settings.DeleteValueAsync(
                DockerCli.ExecutablePathConfigurationKey,
                cancellationToken);
        }
        else
        {
            await packageContext.Settings.SetValueAsync(
                DockerCli.ExecutablePathConfigurationKey,
                dockerCliPath,
                cancellationToken);
        }

        return new DockerExecutionOperationResponse(
            TimeoutSeconds: timeoutSeconds.ToString(),
            DockerCliPath: dockerCliPath,
            Message: "Docker execution settings saved.");
    }

    private async ValueTask<DockerExecutionOperationResponse> AddImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await imageCatalog.AddImageAndListAsync(
            RequireImageReference(request),
            cancellationToken);
        return new DockerExecutionOperationResponse(
            Images: result.Snapshot.Images,
            Message: $"Added Docker image '{result.Image.ImageReference}'. Pull it before assigning it to workspaces.",
            CatalogRevision: result.Snapshot.Revision);
    }

    private async ValueTask<DockerExecutionOperationResponse> DeleteImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var imageReference = RequireExistingImageReference(request);
        var snapshot = await imageCatalog.DeleteImageAndListAsync(imageReference, cancellationToken);
        return new DockerExecutionOperationResponse(
            Images: snapshot.Images,
            Message: $"Deleted Docker image '{imageReference}' from Sunder settings. Existing Docker images on disk were not removed.",
            CatalogRevision: snapshot.Revision);
    }

    private async ValueTask<DockerExecutionOperationResponse> RefreshImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var image = await imageCatalog.RefreshImageAsync(
            RequireImageReference(request),
            cancellationToken);
        var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
        return new DockerExecutionOperationResponse(
            Images: snapshot.Images,
            Message: image.LastMessage ?? $"Refreshed Docker image '{image.ImageReference}'.",
            CatalogRevision: snapshot.Revision);
    }

    private async ValueTask<DockerExecutionOperationResponse> RefreshImagesAsync(
        CancellationToken cancellationToken)
    {
        await imageCatalog.RefreshImagesAsync(cancellationToken);
        var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
        return new DockerExecutionOperationResponse(
            Images: snapshot.Images,
            Message: "Docker image status refreshed.",
            CatalogRevision: snapshot.Revision);
    }

    private async ValueTask<DockerExecutionOperationResponse> PullImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await imageCatalog.PullImageAsync(
            RequireImageReference(request),
            cancellationToken: cancellationToken);
        var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
        return new DockerExecutionOperationResponse(
            Images: snapshot.Images,
            Success: result.Success,
            Message: result.Message,
            CatalogRevision: snapshot.Revision);
    }

    private async ValueTask<DockerExecutionOperationResponse> TestDockerAsync(
        CancellationToken cancellationToken)
    {
        var result = await dockerCliRunner.RunAsync(
            ["version", "--format", "{{.Server.Version}}"],
            30,
            cancellationToken);
        if (result.ExitCode == 0)
        {
            return new DockerExecutionOperationResponse(
                Message: $"Docker is available (server {result.Output.Trim()}).");
        }

        var correlationId = Guid.NewGuid().ToString("N");
        _logger.LogWarning(
            "Docker availability command failed. CorrelationId: {CorrelationId}; ExitCode: {ExitCode}; Output: {Output}",
            correlationId,
            result.ExitCode,
            BoundForLog(result.Output));
        return new DockerExecutionOperationResponse(
            Success: false,
            Error: new DockerExecutionOperationError(
                "docker.command.failed",
                "Docker is unavailable or the configured daemon could not be reached.",
                IsTransient: true,
                CorrelationId: correlationId));
    }

    private async ValueTask<DockerExecutionOperationResponse> GetWorkspaceEditorAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var context = request.EditorContext
            ?? throw new InvalidOperationException("Workspace editor context is required.");
        return new DockerExecutionOperationResponse(
            EditorSections: await workspaceEditor.GetSectionsAsync(context, cancellationToken));
    }

    private async ValueTask<DockerExecutionOperationResponse> SaveWorkspaceEditorAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var context = request.EditorContext
            ?? throw new InvalidOperationException("Workspace editor context is required.");
        var saveRequest = request.EditorSaveRequest
            ?? throw new InvalidOperationException("Workspace editor values are required.");
        return new DockerExecutionOperationResponse(
            EditorSaveResult: await workspaceEditor.SaveSectionAsync(
                context,
                saveRequest,
                cancellationToken));
    }

    private DockerExecutionOperationResponse CreateDomainFailure(
        DockerExecutionOperationKind operation,
        DockerExecutionDomainException exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger.LogWarning(
            exception,
            "Expected Docker operation failure. CorrelationId: {CorrelationId}; Operation: {Operation}; Code: {Code}",
            correlationId,
            operation,
            exception.Code);
        return new DockerExecutionOperationResponse(
            Success: false,
            Error: new DockerExecutionOperationError(
                exception.Code,
                exception.Message,
                exception.IsTransient,
                correlationId));
    }

    private static string RequireImageReference(DockerExecutionOperationRequest request)
        => DockerImageCatalogService.NormalizeImageReference(
            request.ImageReference
            ?? throw new DockerExecutionDomainException(
                "docker.image-reference.required",
                "Docker image reference is required."));

    private static string RequireExistingImageReference(DockerExecutionOperationRequest request)
    {
        var imageReference = request.ImageReference
            ?? throw new DockerExecutionDomainException(
                "docker.image-reference.required",
                "Docker image reference is required.");
        try
        {
            return DockerImageCatalogService.NormalizeImageReference(imageReference);
        }
        catch (DockerExecutionDomainException exception) when (
            exception.Code is "docker.image-reference.unpinned" or "docker.image-reference.latest")
        {
            return DockerImageCatalogService.NormalizeLegacyImageReference(imageReference);
        }
    }

    private static string BoundForLog(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? string.Empty
            : value.Length <= 2048 ? value : value[..2048];
}
