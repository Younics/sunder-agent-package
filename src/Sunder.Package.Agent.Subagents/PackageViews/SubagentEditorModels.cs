using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public enum SubagentStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

internal sealed record SubagentEditorDraft(
    string DisplayName,
    string Description,
    string Instructions,
    ModelBindingSelection ChatBinding,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> CapabilityAssignments);

internal sealed class SubagentEditorDraftComparer : IEqualityComparer<SubagentEditorDraft>
{
    public bool Equals(SubagentEditorDraft? x, SubagentEditorDraft? y)
        => ReferenceEquals(x, y)
            || x is not null
            && y is not null
            && x.DisplayName == y.DisplayName
            && x.Description == y.Description
            && x.Instructions == y.Instructions
            && x.ChatBinding == y.ChatBinding
            && NormalizeAssignments(x.CapabilityAssignments)
                .SequenceEqual(NormalizeAssignments(y.CapabilityAssignments), StringComparer.OrdinalIgnoreCase);

    public int GetHashCode(SubagentEditorDraft obj) => HashCode.Combine(
        obj.DisplayName,
        obj.Description,
        obj.Instructions,
        obj.ChatBinding);

    private static IEnumerable<string> NormalizeAssignments(
        IEnumerable<AgentProfileSelectableCapabilityAssignmentRecord> assignments)
        => assignments.Select(assignment => string.Concat(
                assignment.Kind,
                "\n",
                assignment.SourceId ?? string.Empty,
                "\n",
                assignment.CapabilityId))
            .Order(StringComparer.OrdinalIgnoreCase);
}

internal static class SubagentModelBindingEditor
{
    public static ModelBindingEditorState Create(
        IProviderModelCatalog catalog,
        IPresentationDispatcher? dispatcher = null)
        => new(
            new ProviderModelLoader(catalog),
            new ModelBindingEditorOptions(
                SelectFirstProvider: false,
                NoProvidersText: "No chat providers are installed.",
                NoProviderSelectedText: "Inheriting the parent chat model.",
                LoadingText: "Loading chat provider status...",
                LoadFailurePrefix: "Chat provider status",
                EmptyProviderLabel: "Inherit parent chat model"),
            dispatcher);
}
