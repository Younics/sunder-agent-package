using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.PackageViews;

// RPC snapshots deserialize collection instances anew. Record reference equality
// for those collections must not turn an unchanged snapshot into editor navigation.
internal sealed class AgentProfileSnapshotComparer : IEqualityComparer<AgentProfileRecord>
{
    public bool Equals(AgentProfileRecord? x, AgentProfileRecord? y)
        => ReferenceEquals(x, y) || x is not null && y is not null
            && x with { ModelBindings = y.ModelBindings, SelectableCapabilityAssignments = y.SelectableCapabilityAssignments } == y
            && (x.ModelBindings ?? []).SequenceEqual(y.ModelBindings ?? [])
            && (x.SelectableCapabilityAssignments ?? []).SequenceEqual(y.SelectableCapabilityAssignments ?? []);

    public int GetHashCode(AgentProfileRecord obj) => HashCode.Combine(obj.ProfileId, obj.UpdatedAtUtc);
}

internal sealed class AgentWorkspaceSnapshotComparer : IEqualityComparer<AgentWorkspaceRecord>
{
    public bool Equals(AgentWorkspaceRecord? x, AgentWorkspaceRecord? y)
        => ReferenceEquals(x, y) || x is not null && y is not null
            && x with { Paths = y.Paths, Documents = y.Documents } == y
            && x.Paths.SequenceEqual(y.Paths) && x.Documents.SequenceEqual(y.Documents);

    public int GetHashCode(AgentWorkspaceRecord obj) => HashCode.Combine(obj.WorkspaceId, obj.UpdatedAtUtc);
}

internal static class AgentEditorSnapshotEquality
{
    internal static bool ValuesEqual(AgentEditorFieldValue x, AgentEditorFieldValue y)
        => x.Value == y.Value && (x.Items ?? []).SequenceEqual(y.Items ?? []);

    internal static bool SectionsEqual(AgentEditorSection x, AgentEditorSection y)
        => x with { Fields = y.Fields } == y && x.Fields.Count == y.Fields.Count
            && x.Fields.Zip(y.Fields).All(pair => FieldsEqual(pair.First, pair.Second));

    private static bool FieldsEqual(AgentEditorField x, AgentEditorField y)
        => x with { Options = y.Options, Items = y.Items, Actions = y.Actions } == y
            && (x.Options ?? []).SequenceEqual(y.Options ?? [])
            && (x.Items ?? []).SequenceEqual(y.Items ?? [])
            && (x.Actions ?? []).Count == (y.Actions ?? []).Count
            && (x.Actions ?? []).Zip(y.Actions ?? []).All(pair =>
                pair.First with { Parameters = pair.Second.Parameters } == pair.Second
                && (pair.First.Parameters ?? new Dictionary<string, string?>()).OrderBy(item => item.Key, StringComparer.Ordinal)
                    .SequenceEqual((pair.Second.Parameters ?? new Dictionary<string, string?>()).OrderBy(item => item.Key, StringComparer.Ordinal)));
}
