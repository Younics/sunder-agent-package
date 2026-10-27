using System.Collections.Specialized;
using System.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
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
        Assert.Equal(3, groups.Length);
        Assert.Equal("1 Read · 1 Search", groups[0].State.Summary);
        Assert.Empty(groups[0].State.StatusText);
        Assert.Equal("1 Glob", groups[1].State.Summary);
        Assert.Equal("1 Read · 1 Shell", groups[2].State.Summary);
        Assert.Equal("1!", groups[2].State.StatusText);
        Assert.True(groups[2].State.IsRunning);
        Assert.DoesNotContain(nextRun, projection.Items);
        Assert.DoesNotContain(error, projection.Items);
        Assert.DoesNotContain(background, projection.Items);
        Assert.Contains(permission, projection.Items);
        Assert.Equal(8, source.Count);
        groups[0].State.IsExpanded = true;
        Assert.Equal([commentary, groups[0], read, grep, nextCommentary, groups[1], groups[2], permission, activity, tail], projection.Items);
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
        var read = new Row("read", "Running");
        var shell = new Row("shell", "Running");
        var source = new TranscriptObservableCollection<Row>([read, shell]);
        using var projection = Create(source, new Row(), new Row());
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        Assert.True(group.State.IsRunning);
        read.SetStatus("Failed");
        Assert.Same(group, projection.Items[0]);
        Assert.Equal("1!", group.State.StatusText);
        shell.SetStatus("Failed");
        Assert.Equal("2!", group.State.StatusText);
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
    public void CountsInvocationsByToolCategoryAndRetainsTheHeaderForASingleRemainingCall()
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
        Assert.Same(group, projection.Items[0]);
        Assert.False(group.Disposed);
        Assert.Equal("1 Read", group.State.Summary);
        Assert.DoesNotContain(source[0], projection.Items);
        Assert.Same(group.State.Anchor, projection.RevealAnchor(group.State.Anchor));
    }

    [Fact]
    public void FailedInvocationsAreCountedIndependentlyWithoutPresentationReferenceDeduplication()
    {
        var group = new TranscriptActivityGroupState(new(new object()));
        group.Update(Enumerable.Range(0, 3).Select(_ => new TranscriptActivityMember(new object(), "run",
            "maker_apply", "Failed", false)).ToArray());
        Assert.Equal("3!", group.StatusText);
        Assert.Contains("3 Maker Apply Calls", group.Summary);
        Assert.False(group.IsRunning);
    }

    [Fact]
    public void FirstRunningCallIsCollapsedImmediatelyAndGrowthPreservesItsHeaderAndExpansion()
    {
        var first = new Row("read", "Running");
        var source = new TranscriptObservableCollection<Row>([first]);
        using var projection = Create(source, new Row(), new Row());
        var group = Assert.IsType<GroupRow>(projection.Items[0]);
        Assert.True(group.State.IsRunning);
        Assert.False(group.State.IsExpanded);
        Assert.DoesNotContain(first, projection.Items);
        group.State.IsExpanded = true;
        source.Add(new Row("glob"));
        Assert.Same(group, projection.Items[0]);
        Assert.True(group.State.IsExpanded);
        first.SetStatus("Completed");
        Assert.False(group.State.IsRunning);
        projection.SetDetailed(true);
        Assert.DoesNotContain(group, projection.Items);
        projection.SetDetailed(false);
        Assert.Same(group, projection.Items[0]);
        Assert.True(group.State.IsExpanded);
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

    [Fact]
    public void AlreadyProjectedTimelinePassesThroughWithoutDescribingSubscribingOrGroupingMembers()
    {
        var source = new TranscriptObservableCollection<Row>();
        var activity = new Row();
        var tail = new Row();
        using var projection = new TranscriptActivityProjection<Row>(source,
            _ => throw new InvalidOperationException("Already projected."),
            (_, _) => throw new InvalidOperationException("No legacy groups."), activity, tail);
        projection.SetPassThrough();
        var changes = new List<NotifyCollectionChangedAction>();
        ((INotifyCollectionChanged)projection.Items).CollectionChanged += (_, change) => changes.Add(change.Action);
        var rows = Enumerable.Range(0, 10_000).Select(_ => new Row("read")).ToArray();
        using (source.DeferReconciliation()) foreach (var row in rows) source.Add(row);
        Assert.Equal(rows, projection.Items.Take(rows.Length));
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        changes.Clear();
        foreach (var row in rows) row.SetStatus("Failed");
        Assert.Empty(changes);
        var anchor = new TranscriptActivityGroupAnchor(rows[0].Anchor);
        Assert.Same(anchor, projection.RevealAnchor(anchor));
        Assert.Same(anchor, projection.ResolveVisibleAnchor(anchor));
        var appended = new Row("shell");
        source.Add(appended);
        Assert.Equal([NotifyCollectionChangedAction.Add], changes);
        Assert.Same(appended, projection.Items[rows.Length]);
        changes.Clear();
        var replacement = new Row("read");
        using (source.DeferReconciliation()) source.ReplaceAll([replacement]);
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal([replacement, activity, tail], projection.Items);
        changes.Clear();
        source.Clear();
        Assert.Equal([NotifyCollectionChangedAction.Reset], changes);
        Assert.Equal([activity, tail], projection.Items);
    }

    [Theory]
    [InlineData(0, 0, "", "Completed")]
    [InlineData(1, 0, "", "Running")]
    [InlineData(0, 1, "1!", "Failed")]
    [InlineData(1, 2, "2!", "Running")]
    public void IndexedAndMemberSummariesShareMinimalVisualStatusAndReadableAccessibility(
        int running, int failed, string text, string expectedState)
    {
        var state = new TranscriptActivityGroupState(new(new object()));
        var members = Enumerable.Range(0, running).Select(_ => new TranscriptActivityMember(new object(), "run", "read", "Running", true))
            .Concat(Enumerable.Range(0, failed).Select(_ => new TranscriptActivityMember(new object(), "run", "read", "Failed", false)))
            .Append(new(new object(), "run", "read", "Completed", false)).ToArray();
        state.Update(members);
        Assert.Equal(text, state.StatusText);
        Assert.Equal(expectedState, state.State.ToString());
        Assert.Contains(expectedState, state.AccessibilityText);
        if (failed > 0) Assert.Contains($"{failed} failed", state.AccessibilityText);
        var cursor = new AgentTranscriptInvocationCursor(DateTimeOffset.UtcNow, Guid.NewGuid(), 0, Guid.NewGuid());
        state.UpdateSummary(new(Guid.NewGuid(), cursor, cursor, null, null, 1, 1,
            [new("read", members.Length)], running, 1, failed));
        Assert.Equal(text, state.StatusText);
        Assert.Equal(expectedState, state.State.ToString());
        state.SetLoading(true);
        Assert.Empty(state.LoadingText);
        Assert.Contains("Loading calls", state.AccessibilityText);
        state.SetLoading(false);
        state.SetFailure("Call details could not be loaded. Retry expansion.");
        Assert.Equal("Call details could not be loaded. Retry expansion.", state.LoadingText);
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
