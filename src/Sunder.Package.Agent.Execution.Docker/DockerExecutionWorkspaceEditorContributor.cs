using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class DockerExecutionWorkspaceEditorContributor(DockerExecutionWorkspaceConfigService configService)
    : IAgentWorkspaceEditorContributor
{
    private const string TargetId = "docker";
    private const string SectionId = "docker-execution-settings";
    private const string ImageFieldId = "image";
    private const string ShellPathFieldId = "shell-path";
    private const string RootsFieldId = "allowed-roots";

    public string ContributorId => "sunder.package.agent.execution.docker.workspace-editor";

    public bool CanEdit(AgentWorkspaceEditorContext context)
        => string.Equals(context.TargetId, TargetId, StringComparison.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = configService.GetConfig(context.ConfigurationId);
        IReadOnlyList<AgentEditorSection> sections =
        [
            new AgentEditorSection(
                SectionId,
                "Docker Execution Settings",
                "Docker creates or reuses a workspace container from this image. Each container root can mount a generated or custom host folder.",
                [
                    new AgentEditorField(
                        ImageFieldId,
                        "Docker image:version",
                        AgentEditorFieldKind.Text,
                        Value: config.ImageReference),
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

        return ValueTask.FromResult(sections);
    }

    public ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        AgentWorkspaceEditorContext context,
        AgentEditorSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!string.Equals(request.SectionId, SectionId, StringComparison.OrdinalIgnoreCase))
        {
            return ValueTask.FromResult(AgentEditorSaveResult.Failed("Unknown Docker execution settings section."));
        }

        var image = request.Fields.TryGetValue(ImageFieldId, out var imageValue)
            ? imageValue.Value
            : null;
        if (string.IsNullOrWhiteSpace(image))
        {
            return ValueTask.FromResult(AgentEditorSaveResult.Failed("Docker image cannot be empty."));
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
                return ValueTask.FromResult(AgentEditorSaveResult.Failed("Configure at least one Docker allowed root."));
            }

            var defaultRoot = normalizedMounts.FirstOrDefault(root => root.IsDefault)?.Value ?? normalizedRoots[0];
            var hostRoots = normalizedMounts.ToDictionary(root => root.Value, root => root.SecondaryValue ?? string.Empty, StringComparer.Ordinal);
            configService.SaveConfig(context.ConfigurationId, new DockerExecutionWorkspaceConfig(image, normalizedRoots, defaultRoot, null, shellPath, hostRoots));
            return ValueTask.FromResult(AgentEditorSaveResult.Ok("Docker execution settings saved."));
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return ValueTask.FromResult(AgentEditorSaveResult.Failed(ex.Message));
        }
    }
}
