extern alias MemoryApp;
extern alias MemoryWorker;

using MemoryApp::Sunder.Package.Agent.Memory.Semantic;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class MemoryGraphTests
{
    [Fact]
    public void GraphPageBoundsEncodedBytesWithoutTruncatingIndividualMemories()
    {
        var now = DateTimeOffset.UtcNow;
        var text = new string('\uFFFD', 4096);
        var records = Enumerable.Range(0, 1000).Select(_ => new MemoryWorker::Sunder.Package.Agent.Memory.Semantic.StoredMemoryRecord(
            Guid.NewGuid(), Guid.NewGuid(), "preference", text, text, null, 1, 1, false, "Active", null, now, now, null, 0)).ToArray();
        var page = MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime.ProjectionContent.BoundGraphPage(records, 1000);
        Assert.InRange(page.Memories.Count, 1, 999);
        Assert.True(page.HasMore);
        Assert.All(page.Memories, memory => Assert.Equal(text, memory.Content));
        _ = MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime.ProjectionContent.Create(new(Graph: page));
    }

    [Fact]
    public void GraphUsesRecordedRelationshipsDeduplicatesSharedSourcesAndOmitsMissingNeighbors()
    {
        var now = DateTimeOffset.UtcNow;
        var session = Guid.NewGuid();
        var turn = Guid.NewGuid();
        var newer = new StoredMemoryRecord(Guid.NewGuid(), session, "preference", "New", null, turn, 1, 1, false, "Active", null, now, now, null, 0);
        var old = newer with { MemoryId = Guid.NewGuid(), Content = "Old", SupersededByMemoryId = newer.MemoryId, State = "Superseded" };
        var missing = newer with { MemoryId = Guid.NewGuid(), SupersededByMemoryId = Guid.NewGuid(), SourceTurnId = null };
        var graph = new MemoryGraphState();
        var evidence = new StoredMemoryEvidenceRecord(Guid.NewGuid(), old.MemoryId, session, turn, "Source text", now);

        graph.Apply([old, newer, newer, missing], [evidence, evidence]);

        Assert.Equal(5, graph.Nodes.Count); // Three memories, one shared turn, one evidence record.
        Assert.Equal(4, graph.Edges.Count);
        Assert.Single(graph.Edges, edge => edge.Label == "Replaced by");
        Assert.Single(graph.Edges, edge => edge.Label == "Supports");
        Assert.Equal(2, graph.Edges.Count(edge => edge.Label == "Source"));
        Assert.All(graph.Edges, edge =>
        {
            Assert.Contains(graph.Nodes, node => node.Key == edge.From);
            Assert.Contains(graph.Nodes, node => node.Key == edge.To);
        });
    }
}
