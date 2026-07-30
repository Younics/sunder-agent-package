using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerExecutionWorkspaceEditorContributor(
    DockerExecutionWorkspaceConfigService configService,
    DockerImageCatalogService imageCatalogService)
    : IAgentWorkspaceEditorContributor
{
    private const string PackageId = "sunder.package.agent.execution.docker";
    private const string TargetId = "docker";
    private const string SectionId = "docker-execution-settings";
    private const string ImageFieldId = "image";
    private const string ShellPathFieldId = "shell-path";

    public string ContributorId => "sunder.package.agent.execution.docker.workspace-editor";

    public bool CanEdit(AgentWorkspaceEditorContext context)
        => string.Equals(context.TargetId, TargetId, StringComparison.OrdinalIgnoreCase);

    public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
        var images = await imageCatalogService.RefreshImagesAsync(cancellationToken);
        var imageOptions = images
            .Where(image => image.Status == DockerImageStatus.Ready)
            .Select(image => new AgentEditorOption(image.ImageReference, image.ImageReference))
            .ToArray();
        var imageActions = new List<AgentEditorAction>
        {
            new(
                "refresh-docker-images",
                "Refresh Images",
                AgentEditorActionKind.RefreshField),
        };
        if (imageOptions.Length == 0)
        {
            imageActions.Add(new AgentEditorAction(
                "open-docker-execution-settings",
                "Open Settings",
                AgentEditorActionKind.OpenPackageSettings,
                PackageId));
        }

        IReadOnlyList<AgentEditorSection> sections =
        [
            new AgentEditorSection(
                SectionId,
                "Docker Execution Settings",
                "Docker creates a resource-bounded container with no network, no added Linux capabilities, and writable workspace bind mounts. It reduces exposure but is not a complete security sandbox. Workspace paths are mounted automatically from the main Workspace section.",
                [
                    new AgentEditorField(
                        ImageFieldId,
                        "Docker image",
                        AgentEditorFieldKind.Select,
                        imageOptions.Length == 0
                            ? "Pull at least one configured image in Docker Execution settings."
                            : "Choose a ready image with an explicit tag or sha256 digest.",
                        Value: config.ImageReference,
                        Options: imageOptions)
                    {
                        Actions = imageActions,
                    },
                    new AgentEditorField(
                        ShellPathFieldId,
                        "Shell path inside container",
                        AgentEditorFieldKind.Text,
                        "POSIX-compatible shell used by shell and file tool commands.",
                        Value: config.ShellPath ?? DockerExecutionWorkspaceConfigService.DefaultShellPath),
                ]),
        ];

        return sections;
    }

    public async ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        AgentWorkspaceEditorContext context,
        AgentEditorSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.SectionId, SectionId, StringComparison.OrdinalIgnoreCase))
        {
            return AgentEditorSaveResult.Failed("Unknown Docker execution settings section.");
        }

        var image = request.Fields.TryGetValue(ImageFieldId, out var imageValue)
            ? imageValue.Value
            : null;
        if (string.IsNullOrWhiteSpace(image))
        {
            return AgentEditorSaveResult.Failed("Pull at least one configured Docker image in Docker Execution settings before saving this workspace.");
        }

        try
        {
            image = DockerImageCatalogService.NormalizeImageReference(image);
        }
        catch (InvalidOperationException ex)
        {
            return AgentEditorSaveResult.Failed(ex.Message);
        }

        var imageReadiness = await imageCatalogService.GetReadinessAsync(image, cancellationToken);
        if (!imageReadiness.IsReady)
        {
            return AgentEditorSaveResult.Failed(imageReadiness.Message);
        }

        var shellPath = request.Fields.TryGetValue(ShellPathFieldId, out var shellPathValue)
            ? shellPathValue.Value
            : null;
        try
        {
            var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
            await configService.SaveConfigAsync(context.ConfigurationId, config with
            {
                ImageReference = image,
                ImageReferenceNeedsAttention = false,
                ShellPath = shellPath,
            }, cancellationToken);
            return AgentEditorSaveResult.Ok("Docker execution settings saved.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return AgentEditorSaveResult.Failed(ex.Message);
        }
    }
}
