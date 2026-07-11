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
                "Choose the local shell. Workspace paths are configured in the main Workspace section.",
                [
                    new AgentEditorField(
                        ShellFieldId,
                        "Shell",
                        AgentEditorFieldKind.Select,
                        Value: selectedShellId,
                        Options: shells),
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

        var selectedShellId = request.Fields.TryGetValue(ShellFieldId, out var shellValue)
            ? shellValue.Value
            : null;
        var config = configService.GetConfig(context.ConfigurationId);
        configService.SaveConfig(context.ConfigurationId, config with { SelectedShellId = selectedShellId });
        return ValueTask.FromResult(AgentEditorSaveResult.Ok("Local execution settings saved."));
    }
}
