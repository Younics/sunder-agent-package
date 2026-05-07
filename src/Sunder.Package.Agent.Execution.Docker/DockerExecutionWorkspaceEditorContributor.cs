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
                "Docker creates or reuses a workspace container from this image. Allowed roots are container paths.",
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
                                string.Equals(root, config.DefaultWorkingDirectory, StringComparison.Ordinal)))
                            .ToArray(),
                        AddItemLabel: "Add Container Root",
                        DefaultNewItemValue: "/workspace"),
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
            var normalizedRoots = roots
                .Where(root => !string.IsNullOrWhiteSpace(root.Value))
                .Select(root => DockerExecutionWorkspaceConfigService.NormalizeContainerPath(root.Value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            if (normalizedRoots.Length == 0)
            {
                return ValueTask.FromResult(AgentEditorSaveResult.Failed("Configure at least one Docker allowed root."));
            }

            var defaultRoot = roots.FirstOrDefault(root => root.IsDefault)?.Value ?? normalizedRoots[0];
            configService.SaveConfig(context.ConfigurationId, new DockerExecutionWorkspaceConfig(image, normalizedRoots, defaultRoot, null, shellPath));
            return ValueTask.FromResult(AgentEditorSaveResult.Ok("Docker execution settings saved."));
        }
        catch (InvalidOperationException ex)
        {
            return ValueTask.FromResult(AgentEditorSaveResult.Failed(ex.Message));
        }
    }
}
