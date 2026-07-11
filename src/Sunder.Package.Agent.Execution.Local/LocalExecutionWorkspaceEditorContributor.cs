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

    public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
        var shells = (await shellCatalogService.ListShellsAsync(cancellationToken))
            .Select(shell => new AgentEditorOption(shell.ShellId, shell.DisplayName, $"{shell.ExecutablePath} · {shell.SyntaxKind}"))
            .ToArray();
        var selectedShellId = string.IsNullOrWhiteSpace(config.SelectedShellId)
            ? (await shellCatalogService.GetDefaultShellAsync(cancellationToken)).ShellId
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
            return AgentEditorSaveResult.Failed("Unknown local execution settings section.");
        }

        var selectedShellId = request.Fields.TryGetValue(ShellFieldId, out var shellValue)
            ? shellValue.Value
            : null;
        var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
        await configService.SaveConfigAsync(context.ConfigurationId, config with { SelectedShellId = selectedShellId }, cancellationToken);
        return AgentEditorSaveResult.Ok("Local execution settings saved.");
    }
}
