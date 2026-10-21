using Microsoft.Extensions.Logging;
using Sunder.Agent.Execution.Common;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Execution.Docker;

[SunderService(Key = "management", ContractId = "younics.ai.agent.execution.docker.presentation", ServiceId = "presentation")]
[SunderServiceProvider(ProviderId = "docker.presentation")]
internal sealed partial class DockerExecutionPresentationService(
    IPackageContext packageContext, DockerCliRunner dockerCliRunner, DockerImageCatalogService imageCatalog)
{
    private const int MaximumPathLength = 1024;
    private readonly ILogger _logger = packageContext.Logging.LoggerFactory.CreateLogger<DockerExecutionPresentationService>();

    [SunderMethod(Idempotency = SunderServiceIdempotency.Idempotent)]
    public ValueTask<SunderContent> GetSettingsAsync(CancellationToken cancellationToken = default)
        => CatalogAsync("GetSettings", async () =>
        {
            var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
            return new(
                TimeoutSeconds: await packageContext.Settings.GetValueAsync(DockerExecutionConfiguration.TimeoutKey, cancellationToken)
                    ?? DockerExecutionConfiguration.DefaultTimeoutSeconds,
                DockerCliPath: await packageContext.Settings.GetValueAsync(DockerCli.ExecutablePathConfigurationKey, cancellationToken) ?? string.Empty,
                Images: snapshot.Images, CatalogRevision: snapshot.Revision);
        });

    public async ValueTask<DockerExecutionOperationResponse> SaveSettingsAsync(string? timeoutSeconds, string? dockerCliPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            if (!BoundedValue.TryParseInt32(timeoutSeconds, minimum: 1,
                    maximum: BoundedProcessRunner.MaximumTimeoutSeconds, out var parsedTimeout))
                throw new DockerExecutionDomainException("docker.timeout.invalid",
                    $"Docker command timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.");
            var path = dockerCliPath?.Trim() ?? string.Empty;
            if (path.Length > 0 && (path.Length > MaximumPathLength || !Path.IsPathFullyQualified(path) || !File.Exists(path)))
                throw new DockerExecutionDomainException("docker.cli-path.invalid",
                    "Docker CLI path must be a bounded absolute executable path selected from the local filesystem.");
            await packageContext.Settings.SetValueAsync(DockerExecutionConfiguration.TimeoutKey, parsedTimeout.ToString(), cancellationToken);
            if (path.Length == 0) await packageContext.Settings.DeleteValueAsync(DockerCli.ExecutablePathConfigurationKey, cancellationToken);
            else await packageContext.Settings.SetValueAsync(DockerCli.ExecutablePathConfigurationKey, path, cancellationToken);
            return new(TimeoutSeconds: parsedTimeout.ToString(), DockerCliPath: path, Message: "Docker execution settings saved.");
        }
        catch (DockerExecutionDomainException exception) { return Failure("SaveSettings", exception); }
    }

    public ValueTask<SunderContent> AddImageAsync(string? imageReference, CancellationToken cancellationToken = default)
        => CatalogAsync("AddImage", async () =>
        {
            var result = await imageCatalog.AddImageAndListAsync(RequireImageReference(imageReference), cancellationToken);
            return new(Images: result.Snapshot.Images,
                Message: $"Added Docker image '{result.Image.ImageReference}'. Pull it before assigning it to workspaces.",
                CatalogRevision: result.Snapshot.Revision);
        });

    public ValueTask<SunderContent> DeleteImageAsync(string? imageReference, CancellationToken cancellationToken = default)
        => CatalogAsync("DeleteImage", async () =>
        {
            var reference = RequireExistingImageReference(imageReference);
            var snapshot = await imageCatalog.DeleteImageAndListAsync(reference, cancellationToken);
            return new(Images: snapshot.Images,
                Message: $"Deleted Docker image '{reference}' from Sunder settings. Existing Docker images on disk were not removed.",
                CatalogRevision: snapshot.Revision);
        });

    public ValueTask<SunderContent> RefreshImageAsync(string? imageReference, CancellationToken cancellationToken = default)
        => CatalogAsync("RefreshImage", async () =>
        {
            var image = await imageCatalog.RefreshImageAsync(RequireImageReference(imageReference), cancellationToken);
            var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
            return new(Images: snapshot.Images, Message: image.LastMessage ?? $"Refreshed Docker image '{image.ImageReference}'.",
                CatalogRevision: snapshot.Revision);
        });

    public ValueTask<SunderContent> RefreshImagesAsync(CancellationToken cancellationToken = default)
        => CatalogAsync("RefreshImages", async () =>
        {
            await imageCatalog.RefreshImagesAsync(cancellationToken);
            var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
            return new(Images: snapshot.Images, Message: "Docker image status refreshed.", CatalogRevision: snapshot.Revision);
        });

    public ValueTask<SunderContent> PullImageAsync(string? imageReference, CancellationToken cancellationToken = default)
        => CatalogAsync("PullImage", async () =>
        {
            var result = await imageCatalog.PullImageAsync(RequireImageReference(imageReference), cancellationToken: cancellationToken);
            var snapshot = await imageCatalog.GetSnapshotAsync(cancellationToken);
            return new(Images: snapshot.Images, Success: result.Success, Message: result.Message, CatalogRevision: snapshot.Revision);
        });

    public async ValueTask<DockerExecutionOperationResponse> TestDockerAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var result = await dockerCliRunner.RunAsync(["version", "--format", "{{.Server.Version}}"], 30, cancellationToken);
            if (result.ExitCode == 0) return new(Message: $"Docker is available (server {result.Output.Trim()}).");
            var correlationId = Guid.NewGuid().ToString("N");
            _logger.LogWarning("Docker availability command failed. CorrelationId: {CorrelationId}; ExitCode: {ExitCode}; Output: {Output}",
                correlationId, result.ExitCode, BoundForLog(result.Output));
            return new(Success: false, Error: new("docker.command.failed",
                "Docker is unavailable or the configured daemon could not be reached.", true, correlationId));
        }
        catch (DockerExecutionDomainException exception) { return Failure("TestDocker", exception); }
    }

    private async ValueTask<SunderContent> CatalogAsync(string operation, Func<ValueTask<DockerExecutionOperationResponse>> invoke)
    {
        DockerExecutionOperationResponse response;
        try { response = await invoke().ConfigureAwait(false); }
        catch (DockerExecutionDomainException exception) { response = Failure(operation, exception); }
        return DockerPresentationContent.Create(response);
    }

    private DockerExecutionOperationResponse Failure(string operation, DockerExecutionDomainException exception)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        _logger.LogWarning(exception, "Expected Docker operation failure. CorrelationId: {CorrelationId}; Operation: {Operation}; Code: {Code}",
            correlationId, operation, exception.Code);
        return new(Success: false, Error: new(exception.Code, exception.Message, exception.IsTransient, correlationId));
    }

    private static string RequireImageReference(string? value)
        => DockerImageCatalogService.NormalizeImageReference(value
            ?? throw new DockerExecutionDomainException("docker.image-reference.required", "Docker image reference is required."));

    private static string RequireExistingImageReference(string? value)
    {
        var reference = value ?? throw new DockerExecutionDomainException("docker.image-reference.required", "Docker image reference is required.");
        try { return DockerImageCatalogService.NormalizeImageReference(reference); }
        catch (DockerExecutionDomainException exception) when (exception.Code == "docker.image-reference.unpinned")
        { return DockerImageCatalogService.NormalizeAttentionImageReference(reference); }
    }

    private static string BoundForLog(string? value)
        => string.IsNullOrWhiteSpace(value) ? string.Empty : value.Length <= 2048 ? value : value[..2048];
}
