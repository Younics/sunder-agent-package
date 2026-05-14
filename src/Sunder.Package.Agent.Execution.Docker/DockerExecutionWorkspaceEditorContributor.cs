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
    private const string RootsFieldId = "allowed-roots";

    public string ContributorId => "sunder.package.agent.execution.docker.workspace-editor";

    public bool CanEdit(AgentWorkspaceEditorContext context)
        => string.Equals(context.TargetId, TargetId, StringComparison.OrdinalIgnoreCase);

    public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = configService.GetConfig(context.ConfigurationId);
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
                "Docker creates or reuses a workspace container from a configured image. Pull images in Docker Execution settings before using them here.",
                [
                    new AgentEditorField(
                        ImageFieldId,
                        "Docker image",
                        AgentEditorFieldKind.Select,
                        imageOptions.Length == 0
                            ? "Pull at least one configured image in Docker Execution settings."
                            : "Choose a ready Docker image configured in Docker Execution settings.",
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
                    new AgentEditorField(
                        RootsFieldId,
                        "Allowed container roots",
                        AgentEditorFieldKind.PathList,
                        Items: config.AllowedRoots
                            .Select((root, index) => new AgentEditorListItem(
                                index.ToString(),
                                root,
                                string.Equals(root, config.DefaultWorkingDirectory, StringComparison.Ordinal))
                            {
                                SecondaryValue = configService.ResolveHostPath(config, root),
                            })
                            .ToArray(),
                        AddItemLabel: "Add Container Root",
                        DefaultNewItemValue: DockerExecutionWorkspaceConfigService.DefaultContainerRoot)
                    {
                        ItemValueLabel = "Container root",
                        SecondaryItemValueLabel = "Host folder",
                        UseSecondaryFolderPicker = true,
                    },
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

        var roots = request.Fields.TryGetValue(RootsFieldId, out var rootsValue)
            ? rootsValue.Items ?? []
            : [];
        var shellPath = request.Fields.TryGetValue(ShellPathFieldId, out var shellPathValue)
            ? shellPathValue.Value
            : null;
        try
        {
            var normalizedMounts = new List<AgentEditorListItem>();
            foreach (var root in roots.Where(root => !string.IsNullOrWhiteSpace(root.Value)))
            {
                var containerRoot = DockerExecutionWorkspaceConfigService.NormalizeContainerPath(root.Value);
                if (normalizedMounts.Any(item => string.Equals(item.Value, containerRoot, StringComparison.Ordinal)))
                {
                    continue;
                }

                var hostRoot = string.IsNullOrWhiteSpace(root.SecondaryValue)
                    ? configService.ResolveDefaultHostPath(containerRoot)
                    : DockerExecutionWorkspaceConfigService.NormalizeHostPath(root.SecondaryValue);
                normalizedMounts.Add(new AgentEditorListItem(root.ItemId, containerRoot, root.IsDefault)
                {
                    SecondaryValue = hostRoot,
                });
            }

            var normalizedRoots = normalizedMounts.Select(root => root.Value).ToArray();
            if (normalizedRoots.Length == 0)
            {
                return AgentEditorSaveResult.Failed("Configure at least one Docker allowed root.");
            }

            var defaultRoot = normalizedMounts.FirstOrDefault(root => root.IsDefault)?.Value ?? normalizedRoots[0];
            var hostRoots = normalizedMounts.ToDictionary(root => root.Value, root => root.SecondaryValue ?? string.Empty, StringComparer.Ordinal);
            configService.SaveConfig(context.ConfigurationId, new DockerExecutionWorkspaceConfig(image, normalizedRoots, defaultRoot, null, shellPath, hostRoots));
            return AgentEditorSaveResult.Ok("Docker execution settings saved.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return AgentEditorSaveResult.Failed(ex.Message);
        }
    }
}
