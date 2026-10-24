using System.Collections.Specialized;
using System.ComponentModel;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class TranscriptActivityProjectionTests
{
    [Fact]
    public void GroupsAllToolsIncludingFailuresBetweenMessagesAndRunBoundaries()
    {
        var commentary = new Row();
        var read = new Row("read");
        var grep = new Row("grep");
        var nextCommentary = new Row();
        var nextRun = new Row("glob", scope: "another run");
        var error = new Row("read", "Failed");
        var background = new Row("shell", "Running");
        var permission = new Row();
        var source = new TranscriptObservableCollection<Row>([commentary, read, grep, nextCommentary, nextRun, error, background, permission]);
        var activity = new Row();
        var tail = new Row();
        using var projection = Create(source, activity, tail);

        var groups = projection.Items.OfType<GroupRow>().ToArray();
        Assert.Equal(2, groups.Length);
        Assert.Equal("1 Read · 1 Search", groups[0].State.Summary);
        Assert.Equal(string.Empty, groups[0].State.StatusText);
        Assert.Equal("1 Read · 1 Shell", groups[1].State.Summary);
        Assert.Equal("1 Failed", groups[1].State.StatusText);
        Assert.True(groups[1].State.IsRunning);
        Assert.Contains(nextRun, projection.Items); // Isolated call in a different run stays a normal row.
        Assert.DoesNotContain(error, projection.Items);
        Assert.DoesNotContain(background, projection.Items);
        Assert.Contains(permission, projection.Items);
        Assert.Equal(8, source.Count);
        groups[0].State.IsExpanded = true;
        Assert.Equal([commentary, groups[0], read, grep, nextCommentary, nextRun, groups[1], permission, activity, tail], projection.Items);
        Assert.Same(read, source[1]);
    }

    [Fact]
    public void ExpansionIsIndependentOfDetailsAndSurvivesLiveUpdatesPrependAndViewModeChanges()
    {
        var read = new Row("read", "Running");
        var glob = new Row("glob");
        var source = new TranscriptObservableCollection<Row>([read, glob]);
        var activity = new Row();
        var tail = new Row();
        using var projection = Create(source, activity, tail);
        var notifications = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)projection.Items).CollectionChanged += (_, args) => notifications.Add(args.Action);
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        Assert.True(group.State.IsRunning);
        Assert.Empty(group.State.StatusText);
        var earlier = new Row("grep");
        using (source.DeferReconciliation()) source.Insert(0, earlier);
        Assert.Same(group, projection.Items[0]);
        Assert.Equal("1 Search · 1 Read · 1 Glob", group.State.Summary);
        projection.RevealAnchor(read.Anchor);
        Assert.True(group.State.IsExpanded);
        Assert.Contains(read, projection.Items);
        Assert.False(read.DetailsExpanded);
        read.SetStatus("Completed");
        Assert.Same(group, projection.Items[0]);
        Assert.Empty(group.State.StatusText);
        Assert.False(group.State.IsRunning);
        projection.SetDetailed(true);
        Assert.Equal([earlier, read, glob, activity, tail], projection.Items);
        projection.SetDetailed(false);
        Assert.Same(group, projection.Items[0]);
        Assert.True(group.State.IsExpanded);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Reset, notifications);
        Assert.DoesNotContain(NotifyCollectionChangedAction.Remove, notifications);
        Assert.Same(activity, projection.Items[^2]);
        Assert.Same(tail, projection.Items[^1]);
    }

    [Fact]
    public void FailureAndAmbiguityRemainGroupedWithVisibleOutcomesAndRemovedGroupsArePruned()
    {
        var read = new Row("read", "Prepared");
        var shell = new Row("shell", "Started");
        var source = new TranscriptObservableCollection<Row>([read, shell]);
        using var projection = Create(source, new Row(), new Row());
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        Assert.True(group.State.IsRunning);
        read.SetStatus("Ambiguous");
        Assert.Same(group, projection.Items[0]);
        Assert.Equal("1 Uncertain", group.State.StatusText);
        shell.SetStatus("Failed");
        Assert.Equal("1 Failed · 1 Uncertain", group.State.StatusText);
        Assert.False(group.State.IsRunning);
        Assert.False(group.Disposed);
        source.Clear();
        Assert.True(group.Disposed);
        read.SetStatus("Completed");
        Assert.Equal(2, projection.Items.Count);
    }

    [Fact]
    public void DeferredPageTrim_PreservesTheGroupAnchorAndExpandedMembersUntilItsAuthorityLeavesTheWindow()
    {
        var rows = Enumerable.Range(0, 60).Select(_ => new Row("read")).ToArray();
        var source = new TranscriptObservableCollection<Row>(rows);
        var activity = new Row();
        var tail = new Row();
        using var projection = Create(source, activity, tail);
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        group.State.IsExpanded = true;
        var earlier = new Row("grep");
        using (source.DeferReconciliation())
        {
            source.Insert(0, earlier);
            source.RemoveAt(source.Count - 1);
        }

        Assert.Same(group, projection.Items[0]);
        Assert.True(group.State.IsExpanded);
        Assert.Equal("1 Search · 59 Reads", group.State.Summary);
        Assert.Same(earlier, projection.Items[1]);
        Assert.DoesNotContain(rows[^1], projection.Items);
        Assert.Same(activity, projection.Items[^2]);
        Assert.Same(tail, projection.Items[^1]);
        projection.SetDetailed(true);
        Assert.Same(rows[0].Anchor, projection.RevealAnchor(group.State.Anchor));
        projection.SetDetailed(false);
        using (source.DeferReconciliation())
        {
            source.Remove(earlier);
            source.Remove(rows[0]);
        }
        Assert.True(group.Disposed);
        Assert.NotSame(group, projection.Items[0]);
        Assert.Equal(58, source.Count);
    }

    [Fact]
    public void CountsInvocationsByToolCategoryAndLeavesSingleCallsUngrouped()
    {
        var tools = new[] { "read", "read", "glob", "shell", "shell", "web_search", "web_search", "edit", "apply_patch", "write", "skill", "task", "custom_tool",
            McpToolCardPresentation.CreateStableToolId("server", "read"), McpToolCardPresentation.CreateStableToolId("another", "edit") };
        var source = new TranscriptObservableCollection<Row>(tools.Select(tool => new Row(tool)));
        using var projection = Create(source, new Row(), new Row());
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        Assert.Equal("2 Reads · 1 Glob · 2 Shells · 2 Web Searches · 1 Edit · 1 Patch · 1 Write · 1 Skill · 1 Subagent Task · 1 Custom Tool Call · 2 MCP Tools", group.State.Summary);
        Assert.Empty(group.State.StatusText);
        Assert.False(group.State.IsRunning);
        Assert.Equal(tools.Length, source.Count);
        using (source.DeferReconciliation())
            while (source.Count > 1) source.RemoveAt(source.Count - 1);
        Assert.Same(source[0], projection.Items[0]);
        Assert.True(group.Disposed);
        Assert.Same(source[0].Anchor, projection.RevealAnchor(group.State.Anchor));
    }

    [Fact]
    public void AnchoredWindowRevealsItsTargetInOnePublicationWithoutOverridingLaterUserCollapse()
    {
        var target = new Row("shell", "Running");
        var read = new Row("read");
        var source = new TranscriptObservableCollection<Row>();
        using var projection = Create(source, new Row(), new Row(), () => target.Anchor);
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)projection.Items).CollectionChanged += (_, args) => changes.Add(args.Action);
        source.ReplaceAll([read, target]);
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        Assert.True(group.State.IsExpanded);
        Assert.Contains(target, projection.Items);
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.False(target.DetailsExpanded);
        group.State.IsExpanded = false;
        target.SetStatus("Completed");
        Assert.False(group.State.IsExpanded);
        Assert.DoesNotContain(target, projection.Items);
        Assert.Empty(group.State.StatusText);
    }

    [Fact]
    public void PreservationResolvesCollapsedMembersToTheVisibleHeaderWithoutExpanding()
    {
        var first = new Row("read");
        var second = new Row("shell");
        var source = new TranscriptObservableCollection<Row>([first, second]);
        using var projection = Create(source, new Row(), new Row());
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        Assert.Equal(group.State.Anchor, projection.ResolveVisibleAnchor(second.Anchor));
        Assert.False(group.State.IsExpanded);
        Assert.DoesNotContain(second, projection.Items);
        projection.RevealAnchor(second.Anchor);
        Assert.Equal(second.Anchor, projection.ResolveVisibleAnchor(second.Anchor));
        group.State.IsExpanded = false;
        projection.SetDetailed(true);
        Assert.Equal(first.Anchor, projection.ResolveVisibleAnchor(group.State.Anchor));
    }

    private static TranscriptActivityProjection<Row> Create(TranscriptObservableCollection<Row> source, Row activity, Row tail,
        Func<object?>? initialNavigationAnchor = null)
        => new(source, row => row.ToolId is not null
                ? new(row.Anchor, row.Scope, row.ToolId, row.Status, row.Status is "Prepared" or "Running" or "Started") : null,
            (_, state) => new GroupRow(state), activity, tail) { InitialNavigationAnchor = initialNavigationAnchor };

    private class Row(string? toolId = null, string status = "Completed", string scope = "run") : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public object Anchor { get; } = new();
        public string Scope { get; } = scope;
        public string? ToolId { get; } = toolId;
        public string Status { get; private set; } = status;
        public bool DetailsExpanded => false;
        public void SetStatus(string value) { Status = value; PropertyChanged?.Invoke(this, new("StatusText")); }
    }
    private sealed class GroupRow(TranscriptActivityGroupState state) : Row, IDisposable
    {
        public TranscriptActivityGroupState State { get; } = state;
        public bool Disposed { get; private set; }
        public void Dispose() => Disposed = true;
    }
}
