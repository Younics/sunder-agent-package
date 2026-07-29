using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class AdaptiveListDetailStateTests
{
    public static TheoryData<string[], string, string, string?, string, long> TransitionCases => new()
    {
        { ["snapshot:one,two"], "ExistingDetail", "Wide", "one", "None", 0 },
        { ["compact", "snapshot:one,two"], "List", "Compact", null, "None", 0 },
        { ["snapshot:one,two", "compact"], "List", "Compact", null, "None", 0 },
        { ["compact", "snapshot:one,two", "open:two", "wide"], "ExistingDetail", "Wide", "two", "None", 1 },
        { ["compact", "snapshot:one", "open:one", "back", "wide"], "List", "Wide", null, "None", 2 },
        { ["snapshot:one,two", "open:two", "snapshot:one"], "ExistingDetail", "Wide", "one", "None", 1 },
        { ["compact", "snapshot:one,two", "open:two", "snapshot:one"], "List", "Compact", null, "None", 1 },
        { ["snapshot:one", "new"], "NewDetail", "Wide", null, "None", 1 },
        { ["compact", "snapshot:one", "new", "snapshot:created", "created:created", "ready"], "ExistingDetail", "Compact", "created", "Ready", 1 },
    };

    [Theory]
    [MemberData(nameof(TransitionCases))]
    public void TransitionTable_ProducesOneValidExplicitState(
        string[] actions,
        string expectedRoute,
        string expectedLayout,
        string? expectedSelectedId,
        string expectedDetailPhase,
        long expectedIntentRevision)
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        long createIntent = -1;
        foreach (var action in actions)
        {
            var separator = action.IndexOf(':');
            var name = separator < 0 ? action : action[..separator];
            var argument = separator < 0 ? string.Empty : action[(separator + 1)..];
            switch (name)
            {
                case "snapshot":
                    state.Reconcile(argument.Length == 0
                        ? []
                        : argument.Split(',').Select(id => new TestRow(id, id)).ToArray());
                    break;
                case "compact":
                    state.SetLayout(AdaptiveListDetailLayout.Compact);
                    break;
                case "wide":
                    state.SetLayout(AdaptiveListDetailLayout.Wide);
                    break;
                case "open":
                    state.ShowExistingDetail(argument);
                    break;
                case "back":
                    state.ShowList();
                    break;
                case "new":
                    createIntent = state.ShowNewDetail();
                    break;
                case "created":
                    Assert.True(state.TryShowCreatedDetail(argument, createIntent));
                    break;
                case "ready":
                    Assert.True(state.TrySetDetailReady(state.BeginDetailLoad(
                        TestContext.Current.CancellationToken)));
                    break;
                default:
                    throw new InvalidOperationException($"Unknown transition action '{action}'.");
            }
        }

        Assert.Equal(expectedRoute, state.Route.ToString());
        Assert.Equal(expectedLayout, state.Layout.ToString());
        Assert.Equal(expectedSelectedId, state.SelectedItem?.Id);
        Assert.Equal(expectedDetailPhase, state.DetailPhase.ToString());
        Assert.Equal(expectedIntentRevision, state.IntentRevision);
    }

    [Fact]
    public void PublishedTransitions_NeverExposeExistingDetailWithNullOrStaleSelection()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.PropertyChanged += (_, _) => AssertPublishedState(state, items);

        state.Reconcile([new TestRow("one", "One"), new TestRow("two", "Two")]);
        Assert.True(state.TrySetDetailReady(state.BeginDetailLoad(
            TestContext.Current.CancellationToken)));
        state.ShowExistingDetail("two");
        state.SetLayout(AdaptiveListDetailLayout.Compact);
        state.ShowList();
        var createIntent = state.ShowNewDetail();
        state.Reconcile([new TestRow("created", "Created")]);
        Assert.True(state.TryShowCreatedDetail("created", createIntent));
        state.Reconcile([]);
    }

    [Fact]
    public void CompactBackAndWideResize_PreserveExplicitListIntent()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One")]);
        Assert.Equal(AdaptiveListDetailRoute.ExistingDetail, state.Route);

        state.SetLayout(AdaptiveListDetailLayout.Compact);
        Assert.Equal(AdaptiveListDetailRoute.List, state.Route);
        Assert.Null(state.SelectedItem);

        state.ShowExistingDetail(items[0]);
        Assert.Equal(AdaptiveListDetailRoute.ExistingDetail, state.Route);
        Assert.Same(items[0], state.SelectedItem);

        state.ShowList();
        state.SetLayout(AdaptiveListDetailLayout.Wide);
        state.Reconcile([new TestRow("one", "Updated")]);

        Assert.Equal(AdaptiveListDetailRoute.List, state.Route);
        Assert.Null(state.SelectedItem);
    }

    [Fact]
    public void Reconcile_UpdatesRowsInPlaceAndSelectionBelongsToCurrentCollection()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One"), new TestRow("two", "Two")]);
        state.ShowExistingDetail(items[1]);
        var selected = state.SelectedItem;

        state.Reconcile([new TestRow("two", "Two updated"), new TestRow("one", "One updated")]);

        Assert.Same(selected, state.SelectedItem);
        Assert.Same(state.SelectedItem, items[0]);
        Assert.Equal("Two updated", state.SelectedItem!.Name);
    }

    [Fact]
    public void Reconcile_IgnoresTransientSelectionClearFromCollectionMove()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One"), new TestRow("two", "Two")]);
        state.ShowExistingDetail(items[1]);
        var selected = state.SelectedItem;
        var intentRevision = state.IntentRevision;
        items.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Move)
            {
                state.ShowList();
            }
        };

        state.Reconcile([new TestRow("two", "Two updated"), new TestRow("one", "One updated")]);

        Assert.Equal(intentRevision, state.IntentRevision);
        Assert.Equal(AdaptiveListDetailRoute.ExistingDetail, state.Route);
        Assert.Same(selected, state.SelectedItem);
        Assert.Same(items[0], state.SelectedItem);
    }

    [Fact]
    public void Back_InvalidatesPendingDetailHydration()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One")]);
        var ticket = state.BeginDetailLoad(TestContext.Current.CancellationToken);

        state.ShowList();

        Assert.True(ticket.Request.CancellationToken.IsCancellationRequested);
        Assert.False(state.TrySetDetailReady(ticket));
        Assert.Equal(AdaptiveDetailPhase.None, state.DetailPhase);
    }

    [Fact]
    public void SameKeyActivation_PreservesReadyDetailAndCurrentCollectionObject()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One")]);
        Assert.True(state.TrySetDetailReady(state.BeginDetailLoad(
            TestContext.Current.CancellationToken)));
        var selected = state.SelectedItem;
        var revision = state.IntentRevision;

        state.ShowExistingDetail(new TestRow("one", "Stale object"));

        Assert.Equal(revision, state.IntentRevision);
        Assert.Equal(AdaptiveDetailPhase.Ready, state.DetailPhase);
        Assert.Same(selected, state.SelectedItem);
        Assert.Same(items[0], state.SelectedItem);
    }

    [Fact]
    public void SameKeyActivation_PreservesInFlightDetailAuthority()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One")]);
        var ticket = state.BeginDetailLoad(TestContext.Current.CancellationToken);

        state.ShowExistingDetail(new TestRow("one", "Stale object"));

        Assert.True(state.IsCurrentDetail(ticket));
        Assert.True(state.TrySetDetailReady(ticket));
        Assert.Equal(AdaptiveDetailPhase.Ready, state.DetailPhase);
    }

    [Fact]
    public void PromotedAutomaticSelection_SurvivesCompactLayoutWithoutChangingIntent()
    {
        var items = new ObservableCollection<TestRow>();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One")]);
        var intentRevision = state.IntentRevision;
        var layoutRevision = state.LayoutRevision;
        Assert.True(state.IsSelectionAutomatic);

        state.PromoteSelectionToExplicit();
        state.SetLayout(AdaptiveListDetailLayout.Compact);

        Assert.False(state.IsSelectionAutomatic);
        Assert.Equal(intentRevision, state.IntentRevision);
        Assert.Equal(layoutRevision + 1, state.LayoutRevision);
        Assert.Equal(AdaptiveListDetailRoute.ExistingDetail, state.Route);
        Assert.Same(items[0], state.SelectedItem);
    }

    [Fact]
    public void Create_StaleDeleteRefreshCannotReopenOrRemoveCreatedDetail()
    {
        var items = new ObservableCollection<TestRow>();
        using var requests = new LatestRequestCoordinator();
        using var state = CreateState(items);
        state.SetLayout(AdaptiveListDetailLayout.Compact);
        state.Reconcile([new TestRow("deleted", "Deleted")]);
        state.ShowExistingDetail(items[0]);

        state.ShowList();
        state.Reconcile([]);
        var delayedRuntimeRefresh = requests.Begin("list", TestContext.Current.CancellationToken);

        var createIntent = state.ShowNewDetail();
        requests.Invalidate("list");
        state.Reconcile([new TestRow("created", "Created")]);
        Assert.True(state.TryShowCreatedDetail("created", createIntent));
        var hydration = state.BeginDetailLoad(TestContext.Current.CancellationToken);
        Assert.True(state.TrySetDetailReady(hydration));

        if (requests.IsCurrent(delayedRuntimeRefresh))
        {
            state.Reconcile([]);
        }

        Assert.Equal(AdaptiveListDetailRoute.ExistingDetail, state.Route);
        Assert.Equal(AdaptiveDetailPhase.Ready, state.DetailPhase);
        Assert.Equal("created", state.RouteKey);
        Assert.Same(Assert.Single(items), state.SelectedItem);
    }

    [Fact]
    public async Task SerializedRefreshLoop_CoalescesDirtySignalsWithoutOverlap()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var active = 0;
        var maximumActive = 0;
        using var loop = new SerializedRefreshLoop(async cancellationToken =>
        {
            var currentActive = Interlocked.Increment(ref active);
            maximumActive = Math.Max(maximumActive, currentActive);
            var call = Interlocked.Increment(ref calls);
            if (call == 1)
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }
            Interlocked.Decrement(ref active);
        });

        var first = loop.MarkDirty();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = loop.MarkDirty();
        var third = loop.MarkDirty();
        release.TrySetResult();
        await Task.WhenAll(first, second, third);

        Assert.Equal(2, calls);
        Assert.Equal(1, maximumActive);
    }

    [Fact]
    public async Task SerializedRefreshLoop_DiscardPendingCancelsActiveAndQueuedRefreshes()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        using var loop = new SerializedRefreshLoop(async cancellationToken =>
        {
            Interlocked.Increment(ref calls);
            entered.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            finally
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    cancellationObserved.TrySetResult();
                }
            }
        });

        var refresh = loop.MarkDirty();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        _ = loop.MarkDirty();
        loop.DiscardPending();

        await cancellationObserved.Task.WaitAsync(TestContext.Current.CancellationToken);
        await refresh;
        Assert.Equal(1, calls);
    }

    [Fact]
    public void LatestRequestCoordinator_OnlyNewestGenerationCanApply()
    {
        using var requests = new LatestRequestCoordinator();
        var stale = requests.Begin("list", TestContext.Current.CancellationToken);
        var current = requests.Begin("list", TestContext.Current.CancellationToken);

        Assert.True(stale.CancellationToken.IsCancellationRequested);
        Assert.False(requests.IsCurrent(stale));
        Assert.True(requests.IsCurrent(current));
        Assert.True(requests.Complete(current));
        Assert.False(requests.IsCurrent(current));
    }

    [Fact]
    public void CanceledCurrentDetail_CanRetireWithoutLeavingLoadingLatched()
    {
        var items = new ObservableCollection<TestRow>();
        using var cancellation = new CancellationTokenSource();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One")]);
        var ticket = state.BeginDetailLoad(cancellation.Token);

        cancellation.Cancel();

        Assert.True(state.TryCancelDetailLoad(ticket));
        Assert.Equal(AdaptiveDetailPhase.None, state.DetailPhase);
        Assert.False(state.TrySetDetailReady(ticket));
    }

    [Fact]
    public void CanceledSupersededDetail_CannotClearNewestLoadingState()
    {
        var items = new ObservableCollection<TestRow>();
        using var firstCancellation = new CancellationTokenSource();
        using var state = CreateState(items);
        state.Reconcile([new TestRow("one", "One")]);
        var stale = state.BeginDetailLoad(firstCancellation.Token);
        var current = state.BeginDetailLoad(TestContext.Current.CancellationToken);

        firstCancellation.Cancel();

        Assert.False(state.TryCancelDetailLoad(stale));
        Assert.Equal(AdaptiveDetailPhase.Loading, state.DetailPhase);
        Assert.True(state.TrySetDetailReady(current));
    }

    private static KeyedAdaptiveListDetailState<string, TestRow> CreateState(
        ObservableCollection<TestRow> items)
        => new(
            items,
            static row => row.Id,
            static (current, incoming) => current.Name = incoming.Name,
            StringComparer.Ordinal);

    private static void AssertPublishedState(
        KeyedAdaptiveListDetailState<string, TestRow> state,
        ObservableCollection<TestRow> items)
    {
        if (state.Route == AdaptiveListDetailRoute.ExistingDetail)
        {
            var selected = Assert.IsType<TestRow>(state.SelectedItem);
            Assert.Equal(state.RouteKey, selected.Id);
            Assert.Same(selected, Assert.Single(items, item => item.Id == selected.Id));
        }
        else if (state.Route == AdaptiveListDetailRoute.List)
        {
            Assert.Null(state.SelectedItem);
        }

        if (state.DetailPhase == AdaptiveDetailPhase.Ready)
        {
            Assert.Equal(AdaptiveListDetailRoute.ExistingDetail, state.Route);
            Assert.Equal(state.RouteKey, state.ReadyKey);
        }
    }

    private sealed class TestRow(string id, string name)
    {
        public string Id { get; } = id;

        public string Name { get; set; } = name;
    }
}
