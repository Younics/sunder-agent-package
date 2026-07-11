using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.PackageViews;

internal sealed record ProfileEditorDraft(
    string DisplayName,
    string Description,
    string Instructions,
    ModelBindingSelection ChatBinding,
    ModelBindingSelection EmbeddingBinding,
    string? BehaviorLoopId,
    string? BehaviorLoopSourceId,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> CapabilityAssignments);

internal sealed class ProfileEditorDraftComparer : IEqualityComparer<ProfileEditorDraft>
{
    public bool Equals(ProfileEditorDraft? x, ProfileEditorDraft? y)
        => ReferenceEquals(x, y)
            || x is not null
            && y is not null
            && x.DisplayName == y.DisplayName
            && x.Description == y.Description
            && x.Instructions == y.Instructions
            && x.ChatBinding == y.ChatBinding
            && x.EmbeddingBinding == y.EmbeddingBinding
            && x.BehaviorLoopId == y.BehaviorLoopId
            && x.BehaviorLoopSourceId == y.BehaviorLoopSourceId
            && NormalizeAssignments(x.CapabilityAssignments)
                .SequenceEqual(NormalizeAssignments(y.CapabilityAssignments), StringComparer.OrdinalIgnoreCase);

    public int GetHashCode(ProfileEditorDraft obj) => HashCode.Combine(
        obj.DisplayName,
        obj.Description,
        obj.Instructions,
        obj.ChatBinding,
        obj.EmbeddingBinding,
        obj.BehaviorLoopId,
        obj.BehaviorLoopSourceId);

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
