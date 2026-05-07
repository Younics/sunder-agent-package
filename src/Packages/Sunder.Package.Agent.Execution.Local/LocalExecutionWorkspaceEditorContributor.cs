using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class LocalExecutionWorkspaceEditorContributor(
    LocalExecutionWorkspaceConfigService configService,
    LocalShellCatalogService shellCatalogService) : IAgentWorkspaceEditorContributor
{
    private const string TargetId = "local";
    private const string SectionId = "local-execution-settings";
    private const string ShellFieldId = "shell";
    private const string RootsFieldId = "allowed-roots";

    public string ContributorId => "sunder.package.agent.execution.local.workspace-editor";

    public bool CanEdit(AgentWorkspaceEditorContext context)
        => string.Equals(context.TargetId, TargetId, StringComparison.OrdinalIgnoreCase);

    public ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = configService.GetConfig(context.ConfigurationId);
        var shells = shellCatalogService.ListShells()
            .Select(shell => new AgentEditorOption(shell.ShellId, shell.DisplayName, $"{shell.ExecutablePath} · {shell.SyntaxKind}"))
            .ToArray();
        var selectedShellId = string.IsNullOrWhiteSpace(config.SelectedShellId)
            ? shellCatalogService.GetDefaultShell().ShellId
            : config.SelectedShellId;

        IReadOnlyList<AgentEditorSection> sections =
        [
            new AgentEditorSection(
                SectionId,
                "Local Execution Settings",
                "Allowed roots are local folders. Select one row at a time and mark one root as the default working directory.",
                [
                    new AgentEditorField(
                        ShellFieldId,
                        "Shell",
                        AgentEditorFieldKind.Select,
                        Value: selectedShellId,
                        Options: shells),
                    new AgentEditorField(
                        RootsFieldId,
                        "Allowed roots",
                        AgentEditorFieldKind.PathList,
                        Items: config.AllowedRoots
                            .Select((root, index) => new AgentEditorListItem(
                                index.ToString(),
                                root,
                                string.Equals(root, config.DefaultWorkingDirectory, StringComparison.OrdinalIgnoreCase)))
                            .ToArray(),
                        AddItemLabel: "Add Folder...",
                        UseFolderPicker: true),
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
            return ValueTask.FromResult(AgentEditorSaveResult.Failed("Unknown local execution settings section."));
        }

        var roots = request.Fields.TryGetValue(RootsFieldId, out var rootsValue)
            ? rootsValue.Items ?? []
            : [];
        var normalizedRoots = new List<string>();
        foreach (var root in roots)
        {
            if (string.IsNullOrWhiteSpace(root.Value))
            {
                continue;
            }

            var fullPath = Path.GetFullPath(LocalExecutionWorkspaceConfigService.ExpandPath(root.Value));
            if (!Directory.Exists(fullPath))
            {
                return ValueTask.FromResult(AgentEditorSaveResult.Failed($"Folder does not exist: {fullPath}"));
            }

            if (!normalizedRoots.Contains(fullPath, StringComparer.OrdinalIgnoreCase))
            {
                normalizedRoots.Add(fullPath);
            }
        }

        var defaultRoot = roots.FirstOrDefault(root => root.IsDefault)?.Value ?? normalizedRoots.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(defaultRoot))
        {
            defaultRoot = Path.GetFullPath(LocalExecutionWorkspaceConfigService.ExpandPath(defaultRoot));
        }

        var selectedShellId = request.Fields.TryGetValue(ShellFieldId, out var shellValue)
            ? shellValue.Value
            : null;
        configService.SaveConfig(context.ConfigurationId, new LocalExecutionWorkspaceConfig(normalizedRoots, defaultRoot, selectedShellId));
        return ValueTask.FromResult(AgentEditorSaveResult.Ok("Local execution settings saved."));
    }
}
