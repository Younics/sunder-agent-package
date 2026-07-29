extern alias AgentCore;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Xunit;
using CoreViews = AgentCore::Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Tests;

public sealed class TranscriptItemsProjectionTests
{
    [Fact]
    public void Projection_MirrorsEverySourceMutationWithoutReplacingPermanentSuffix()
    {
        var source = new ObservableCollection<TestItem>(
        [
            new TestItem("one"),
            new TestItem("two"),
            new TestItem("three"),
        ]);
        var activity = new TestItem("activity");
        var tail = new TestItem("tail");
        using var projection = new CoreViews.TranscriptItemsProjection<TestItem>(
            source,
            activity,
            tail);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)projection.Items).CollectionChanged +=
            (_, eventArgs) => changes.Add(eventArgs);

        AssertProjection(source, projection.Items, activity, tail);
        source.Add(new TestItem("four"));
        AssertProjection(source, projection.Items, activity, tail);
        source.RemoveAt(1);
        AssertProjection(source, projection.Items, activity, tail);
        source[1] = new TestItem("replacement");
        AssertProjection(source, projection.Items, activity, tail);
        source.Move(2, 0);
        AssertProjection(source, projection.Items, activity, tail);
        source.Clear();
        AssertProjection(source, projection.Items, activity, tail);

        Assert.DoesNotContain(changes, change => Contains(change.OldItems, activity, tail));
        Assert.DoesNotContain(changes, change => Contains(change.NewItems, activity, tail));
    }

    [Fact]
    public void Projection_KeepsSuffixAcrossPagingAndNinetyToSixtyTrim()
    {
        var source = new ObservableCollection<TestItem>(
            Enumerable.Range(30, 60).Select(index => new TestItem($"row-{index}")));
        var activity = new TestItem("activity");
        var tail = new TestItem("tail");
        using var projection = new CoreViews.TranscriptItemsProjection<TestItem>(
            source,
            activity,
            tail);

        for (var index = 29; index >= 0; index--)
        {
            source.Insert(0, new TestItem($"row-{index}"));
        }
        Assert.Equal(90, source.Count);
        AssertProjection(source, projection.Items, activity, tail);

        for (var index = 0; index < 30; index++)
        {
            source.RemoveAt(0);
        }

        Assert.Equal(60, source.Count);
        AssertProjection(source, projection.Items, activity, tail);
        Assert.Equal("row-30", projection.Items[0].Name);
        Assert.Equal("row-89", projection.Items[59].Name);
    }

    [Fact]
    public void Projection_DisposeStopsMirroringWithoutMutatingSuffix()
    {
        var source = new ObservableCollection<TestItem> { new("row") };
        var activity = new TestItem("activity");
        var tail = new TestItem("tail");
        var projection = new CoreViews.TranscriptItemsProjection<TestItem>(
            source,
            activity,
            tail);

        projection.Dispose();
        projection.Dispose();
        source.Add(new TestItem("after-dispose"));

        Assert.Equal(3, projection.Items.Count);
        Assert.Same(activity, projection.Items[^2]);
        Assert.Same(tail, projection.Items[^1]);
    }

    [Fact]
    public void DeferredReconciliation_PreservesStableSuffixIdentityWithoutReset()
    {
        var first = new TestItem("first");
        var second = new TestItem("second");
        var retained = new TestItem("retained");
        var replacement = new TestItem("replacement");
        var items = new CoreViews.TranscriptObservableCollection<TestItem>(
            [first, second, retained]);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        items.CollectionChanged += (_, change) => changes.Add(change);

        using (items.DeferReconciliation())
        {
            items.Insert(0, replacement);
            items.Remove(first);
        }

        Assert.Equal([replacement, second, retained], items);
        Assert.DoesNotContain(changes, change => change.Action == NotifyCollectionChangedAction.Reset);
        Assert.DoesNotContain(changes, item => Contains(item.OldItems, retained, retained));
        Assert.DoesNotContain(changes, item => Contains(item.NewItems, retained, retained));
    }

    [Fact]
    public void Projection_DeferredPrependAndTailTrimPreserveRetainedItemNotifications()
    {
        var retained = new TestItem("retained");
        var source = new CoreViews.TranscriptObservableCollection<TestItem>(
            [retained, new("second"), new("trim-one"), new("trim-two")]);
        var activity = new TestItem("activity");
        var tail = new TestItem("tail");
        using var projection = new CoreViews.TranscriptItemsProjection<TestItem>(source, activity, tail);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)projection.Items).CollectionChanged += (_, change) => changes.Add(change);

        using (source.DeferReconciliation())
        {
            source.Insert(0, new TestItem("prepend-one"));
            source.Insert(1, new TestItem("prepend-two"));
            source.RemoveAt(source.Count - 1);
            source.RemoveAt(source.Count - 1);
        }

        AssertProjection(source, projection.Items, activity, tail);
        Assert.Equal(
            [NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Add],
            changes.Select(change => change.Action));
        Assert.DoesNotContain(changes, change => Contains(change.OldItems, retained));
        Assert.DoesNotContain(changes, change => Contains(change.NewItems, retained));
    }

    [Fact]
    public void Projection_DeferredHeadTrimAndAppendPreserveRetainedItemNotifications()
    {
        var retained = new TestItem("retained");
        var source = new CoreViews.TranscriptObservableCollection<TestItem>(
            [new("trim-one"), new("trim-two"), new("first"), retained]);
        var activity = new TestItem("activity");
        var tail = new TestItem("tail");
        using var projection = new CoreViews.TranscriptItemsProjection<TestItem>(source, activity, tail);
        var changes = new List<NotifyCollectionChangedEventArgs>();
        ((INotifyCollectionChanged)projection.Items).CollectionChanged += (_, change) => changes.Add(change);

        using (source.DeferReconciliation())
        {
            source.RemoveAt(0);
            source.RemoveAt(0);
            source.Add(new TestItem("append-one"));
            source.Add(new TestItem("append-two"));
        }

        AssertProjection(source, projection.Items, activity, tail);
        Assert.Equal(
            [NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Add],
            changes.Select(change => change.Action));
        Assert.DoesNotContain(changes, change => Contains(change.OldItems, retained));
        Assert.DoesNotContain(changes, change => Contains(change.NewItems, retained));
    }

    [Fact]
    public void DeferredReconciliation_CommitsSourceWhenProjectionCallbackFails()
    {
        var replacement = new TestItem("replacement");
        var source = new CoreViews.TranscriptObservableCollection<TestItem>([new("original")]);
        source.DeferredReconciliationCommitting += _ =>
            throw new InvalidOperationException("projection failed");
        var reconciliation = source.DeferReconciliation();
        source[0] = replacement;

        var exception = Assert.Throws<InvalidOperationException>(reconciliation.Dispose);

        Assert.Equal("projection failed", exception.Message);
        Assert.Same(replacement, Assert.Single(source));
    }

    private static void AssertProjection(
        IReadOnlyList<TestItem> source,
        IReadOnlyList<TestItem> items,
        TestItem activity,
        TestItem tail)
    {
        Assert.Equal(source.Count + 2, items.Count);
        for (var index = 0; index < source.Count; index++)
        {
            Assert.Same(source[index], items[index]);
        }
        Assert.Same(activity, items[^2]);
        Assert.Same(tail, items[^1]);
    }

    private static bool Contains(
        System.Collections.IList? items,
        TestItem activity,
        TestItem tail)
        => items?.Cast<object>().Any(item => ReferenceEquals(item, activity) || ReferenceEquals(item, tail)) == true;

    private static bool Contains(System.Collections.IList? items, TestItem expected)
        => items?.Cast<object>().Any(item => ReferenceEquals(item, expected)) == true;

    private sealed record TestItem(string Name);
}
