using IWorkspaceEditorService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Workspace_Editor.IWorkspaceEditorService;
using Sunder.Sdk.Services;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local;

[SunderServiceProvider(typeof(IWorkspaceEditorService), ProviderId = "local.workspace.editor")]
public sealed partial class LocalExecutionWorkspaceEditorContributor(
    LocalExecutionWorkspaceConfigService configService,
    LocalShellCatalogService shellCatalogService) : IWorkspaceEditorService
{
    private const string TargetId = "local";
    private const string SectionId = "local-execution-settings";
    private const string ShellFieldId = "shell";

    public string ContributorId => "younics.ai.agent.execution.local.workspace-editor";

    public ValueTask<AgentServiceIdentity> DescribeAsync(CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new AgentServiceIdentity { Id = ContributorId, DisplayName = ContributorId }); }

    public ValueTask<bool> CanEditAsync(AgentWorkspaceEditorContext context, CancellationToken token = default)
    { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(string.Equals(context.TargetId, TargetId, StringComparison.OrdinalIgnoreCase)); }

    public async ValueTask<IReadOnlyList<AgentEditorSection>> GetSectionsAsync(
        AgentWorkspaceEditorContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
        var shells = (await shellCatalogService.ListShellsAsync(cancellationToken))
            .Select(shell => new AgentEditorOption { Value = shell.ShellId, Label = shell.DisplayName, Description = $"{shell.ExecutablePath} · {shell.SyntaxKind}" })
            .ToArray();
        var selectedShellId = string.IsNullOrWhiteSpace(config.SelectedShellId)
            ? (await shellCatalogService.GetDefaultShellAsync(cancellationToken)).ShellId
            : config.SelectedShellId;

        IReadOnlyList<AgentEditorSection> sections =
        [
            new AgentEditorSection
            {
                SectionId = SectionId, Title = "Local Execution Settings",
                Description = "Choose the local shell. Workspace paths are configured in the main Workspace section.",
                Fields = [new()
                {
                    FieldId = ShellFieldId, Label = "Shell", Kind = AgentEditorFieldKind.Select, Value = selectedShellId, Options = shells,
                    Description = null, Items = null, AddItemLabel = null, UseFolderPicker = false, DefaultNewItemValue = null,
                    ItemValueLabel = null, SecondaryItemValueLabel = null, UseSecondaryFolderPicker = false, DefaultNewSecondaryItemValue = null, Actions = null,
                }],
            },
        ];

        return sections;
    }

    public async ValueTask<AgentEditorSaveResult> SaveSectionAsync(
        CYounics_Ai_Agent_Workspace_Editor_WorkspaceEditorSaveSectionRequest invocation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (context, request) = (invocation.Context, invocation.Request);
        if (!string.Equals(request.SectionId, SectionId, StringComparison.OrdinalIgnoreCase))
        {
            return new() { Success = false, Message = "Unknown local execution settings section." };
        }

        var selectedShellId = request.Fields.TryGetValue(ShellFieldId, out var shellValue)
            ? shellValue.Value
            : null;
        var config = await configService.GetConfigAsync(context.ConfigurationId, cancellationToken);
        await configService.SaveConfigAsync(context.ConfigurationId, config with { SelectedShellId = selectedShellId }, cancellationToken);
        return new() { Success = true, Message = "Local execution settings saved." };
    }
}
