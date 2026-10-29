namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

internal sealed record MemoryGraphNode(string Key, Guid? MemoryId, string Title, string Preview);
internal sealed record MemoryGraphEdge(string From, string To, string Label);

// Only recorded relationships become edges; category is a label, not a relationship.
internal sealed class MemoryGraphState
{
    public IReadOnlyList<MemoryGraphNode> Nodes { get; private set; } = [];
    public IReadOnlyList<MemoryGraphEdge> Edges { get; private set; } = [];
    public Guid? SessionId { get; private set; }

    public void Apply(IEnumerable<StoredMemoryRecord> memories, IEnumerable<StoredMemoryEvidenceRecord>? evidence = null, Guid? sessionId = null)
    {
        SessionId = sessionId;
        var nodes = new Dictionary<string, MemoryGraphNode>(StringComparer.Ordinal);
        var edges = new HashSet<MemoryGraphEdge>();
        static string MemoryKey(Guid id) => "memory:" + id;
        foreach (var memory in memories.DistinctBy(item => item.MemoryId))
        {
            SessionId ??= memory.SessionId;
            if (memory.SessionId != SessionId) continue;
            var key = MemoryKey(memory.MemoryId);
            nodes[key] = new(key, memory.MemoryId,
                (memory.IsPinned ? "Pinned · " : "") + memory.Category.Replace('-', ' ')
                + (memory.State == MemoryRecordStates.Active ? "" : " · " + memory.State), memory.Content);
            if (memory.SourceTurnId is { } source)
            {
                var sourceKey = "turn:" + source;
                nodes.TryAdd(sourceKey, new(sourceKey, null, "Source turn", source.ToString("N")[..8]));
                edges.Add(new(sourceKey, key, "Source"));
            }
            if (memory.SupersededByMemoryId is { } target && target != memory.MemoryId)
                edges.Add(new(key, MemoryKey(target), "Replaced by"));
        }
        foreach (var item in evidence ?? [])
        {
            var memoryKey = MemoryKey(item.MemoryId);
            if (!nodes.ContainsKey(memoryKey)) continue;
            var key = "evidence:" + item.EvidenceId;
            nodes[key] = new(key, null, "Evidence", item.EvidenceText ?? "Recorded evidence");
            edges.Add(new(key, memoryKey, "Supports"));
        }
        // A filtered or unloaded neighbor is not an invented memory node.
        Nodes = nodes.Values.ToArray();
        Edges = edges.Where(edge => nodes.ContainsKey(edge.From) && nodes.ContainsKey(edge.To))
            .OrderBy(edge => edge.From, StringComparer.Ordinal).ThenBy(edge => edge.To, StringComparer.Ordinal).ToArray();
    }
}
