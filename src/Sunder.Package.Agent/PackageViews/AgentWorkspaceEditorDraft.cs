using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

internal sealed record AgentWorkspaceEditorDraft(
    string DisplayName,
    string Description,
    IReadOnlyList<AgentWorkspacePathRecord> Paths,
    IReadOnlyList<AgentWorkspaceDocumentRecord> Documents,
    string? ExecutionTargetId,
    IReadOnlyList<AgentEditorSectionViewModel> EditorSections);

internal sealed class AgentWorkspaceDraftState(
    AgentWorkspaceEditorDraft draft,
    long revision,
    bool isDirty)
{
    public AgentWorkspaceEditorDraft Draft { get; set; } = draft;

    public long Revision { get; set; } = revision;

    public bool IsDirty { get; set; } = isDirty;
}
