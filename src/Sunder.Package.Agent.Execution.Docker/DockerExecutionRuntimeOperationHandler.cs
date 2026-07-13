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

    public async ValueTask<DockerExecutionOperationResponse> HandleAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken = default)
        => request.Kind switch
        {
            DockerExecutionOperationKind.GetSettings => await GetSettingsAsync(cancellationToken),
            DockerExecutionOperationKind.SaveSettings => await SaveSettingsAsync(request, cancellationToken),
            DockerExecutionOperationKind.AddImage => await AddImageAsync(request, cancellationToken),
            DockerExecutionOperationKind.DeleteImage => await DeleteImageAsync(request, cancellationToken),
            DockerExecutionOperationKind.RefreshImage => await RefreshImageAsync(request, cancellationToken),
            DockerExecutionOperationKind.RefreshImages => new(Images: await imageCatalog.RefreshImagesAsync(cancellationToken), Message: "Docker image status refreshed."),
            DockerExecutionOperationKind.PullImage => await PullImageAsync(request, cancellationToken),
            DockerExecutionOperationKind.TestDocker => await TestDockerAsync(cancellationToken),
            DockerExecutionOperationKind.GetWorkspaceEditor => await GetWorkspaceEditorAsync(request, cancellationToken),
            DockerExecutionOperationKind.SaveWorkspaceEditor => await SaveWorkspaceEditorAsync(request, cancellationToken),
            _ => throw new InvalidOperationException("Unknown Docker execution operation."),
        };

    private async ValueTask<DockerExecutionOperationResponse> GetSettingsAsync(CancellationToken cancellationToken)
        => new(
            await packageContext.Settings.GetValueAsync(DockerExecutionConfiguration.TimeoutKey, cancellationToken)
                ?? DockerExecutionConfiguration.DefaultTimeoutSeconds,
            await packageContext.Settings.GetValueAsync(DockerCli.ExecutablePathConfigurationKey, cancellationToken) ?? string.Empty,
            await imageCatalog.ListImagesAsync(cancellationToken));

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
            throw new InvalidOperationException($"Docker command timeout must be between 1 and {BoundedProcessRunner.MaximumTimeoutSeconds} seconds.");
        }

        var dockerCliPath = request.DockerCliPath?.Trim() ?? string.Empty;
        if (dockerCliPath.Length > 0)
        {
            if (dockerCliPath.Length > MaximumPathLength || !Path.IsPathFullyQualified(dockerCliPath) || !File.Exists(dockerCliPath))
            {
                throw new InvalidOperationException("Docker CLI path must be a bounded absolute executable path selected from the local filesystem.");
            }
        }

        await packageContext.Settings.SetValueAsync(DockerExecutionConfiguration.TimeoutKey, timeoutSeconds.ToString(), cancellationToken);
        if (dockerCliPath.Length == 0)
        {
            await packageContext.Settings.DeleteValueAsync(DockerCli.ExecutablePathConfigurationKey, cancellationToken);
        }
        else
        {
            await packageContext.Settings.SetValueAsync(DockerCli.ExecutablePathConfigurationKey, dockerCliPath, cancellationToken);
        }

        return new(timeoutSeconds.ToString(), dockerCliPath, Message: "Docker execution settings saved.");
    }

    private async ValueTask<DockerExecutionOperationResponse> AddImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var image = await imageCatalog.AddImageAsync(RequireImageReference(request), cancellationToken);
        return new(
            Images: await imageCatalog.ListImagesAsync(cancellationToken),
            Message: $"Added Docker image '{image.ImageReference}'. Pull it before assigning it to workspaces.");
    }

    private async ValueTask<DockerExecutionOperationResponse> DeleteImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var imageReference = RequireImageReference(request);
        await imageCatalog.DeleteImageAsync(imageReference, cancellationToken);
        return new(
            Images: await imageCatalog.ListImagesAsync(cancellationToken),
            Message: $"Deleted Docker image '{imageReference}' from Sunder settings. Existing Docker images on disk were not removed.");
    }

    private async ValueTask<DockerExecutionOperationResponse> RefreshImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var image = await imageCatalog.RefreshImageAsync(RequireImageReference(request), cancellationToken);
        return new(
            Images: await imageCatalog.ListImagesAsync(cancellationToken),
            Message: image.LastMessage ?? $"Refreshed Docker image '{image.ImageReference}'.");
    }

    private async ValueTask<DockerExecutionOperationResponse> PullImageAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var result = await imageCatalog.PullImageAsync(RequireImageReference(request), cancellationToken: cancellationToken);
        return new(
            Images: await imageCatalog.ListImagesAsync(cancellationToken),
            Success: result.Success,
            Message: result.Message);
    }

    private async ValueTask<DockerExecutionOperationResponse> TestDockerAsync(CancellationToken cancellationToken)
    {
        var result = await dockerCliRunner.RunAsync(["version", "--format", "{{.Server.Version}}"], 30, cancellationToken);
        return result.ExitCode == 0
            ? new(Message: $"Docker is available (server {result.Output.Trim()}).")
            : new(Success: false, Message: string.IsNullOrWhiteSpace(result.Output) ? "Docker is unavailable." : result.Output.Trim());
    }

    private async ValueTask<DockerExecutionOperationResponse> GetWorkspaceEditorAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var context = request.EditorContext ?? throw new InvalidOperationException("Workspace editor context is required.");
        return new(EditorSections: await workspaceEditor.GetSectionsAsync(context, cancellationToken));
    }

    private async ValueTask<DockerExecutionOperationResponse> SaveWorkspaceEditorAsync(
        DockerExecutionOperationRequest request,
        CancellationToken cancellationToken)
    {
        var context = request.EditorContext ?? throw new InvalidOperationException("Workspace editor context is required.");
        var saveRequest = request.EditorSaveRequest ?? throw new InvalidOperationException("Workspace editor values are required.");
        return new(EditorSaveResult: await workspaceEditor.SaveSectionAsync(context, saveRequest, cancellationToken));
    }

    private static string RequireImageReference(DockerExecutionOperationRequest request)
        => DockerImageCatalogService.NormalizeImageReference(
            request.ImageReference ?? throw new InvalidOperationException("Docker image reference is required."));
}
