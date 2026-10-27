using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Sunder.Package.Agent.Shared.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class TranscriptItemsProjection<TItem> : IDisposable
    where TItem : class
{
    private readonly ObservableCollection<TItem> _source;
    private readonly TranscriptObservableCollection<TItem> _items;
    private readonly TranscriptObservableCollection<TItem>? _reconcilingSource;
    private readonly TItem[] _suffix;
    private bool _disposed;

    public TranscriptItemsProjection(
        ObservableCollection<TItem> source,
        params TItem[] suffix)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(suffix);
        if (suffix.Any(item => item is null))
        {
            throw new ArgumentException("Transcript suffix items cannot be null.", nameof(suffix));
        }

        _source = source;
        _suffix = [.. suffix];
        _items = new TranscriptObservableCollection<TItem>([.. source, .. _suffix]);
        Items = new ReadOnlyObservableCollection<TItem>(_items);
        _reconcilingSource = source as TranscriptObservableCollection<TItem>;
        if (_reconcilingSource is not null)
        {
            _reconcilingSource.DeferredReconciliationCommitting += OnDeferredReconciliationCommitting;
        }
        _source.CollectionChanged += OnSourceCollectionChanged;
    }

    public ReadOnlyObservableCollection<TItem> Items { get; }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _source.CollectionChanged -= OnSourceCollectionChanged;
        if (_reconcilingSource is not null)
        {
            _reconcilingSource.DeferredReconciliationCommitting -= OnDeferredReconciliationCommitting;
        }
    }

    private void OnSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs eventArgs)
    {
        if (_reconcilingSource?.IsPublishingDeferredReconciliation == true)
        {
            return;
        }

        switch (eventArgs.Action)
        {
            case NotifyCollectionChangedAction.Add when eventArgs.NewStartingIndex >= 0:
                InsertRange(eventArgs.NewStartingIndex, eventArgs.NewItems);
                break;
            case NotifyCollectionChangedAction.Remove when eventArgs.OldStartingIndex >= 0:
                RemoveRange(eventArgs.OldStartingIndex, eventArgs.OldItems?.Count ?? 0);
                break;
            case NotifyCollectionChangedAction.Replace
                when eventArgs.NewStartingIndex >= 0
                     && eventArgs.NewItems?.Count == eventArgs.OldItems?.Count:
                ReplaceRange(eventArgs.NewStartingIndex, eventArgs.NewItems);
                break;
            case NotifyCollectionChangedAction.Move
                when eventArgs.OldStartingIndex >= 0
                     && eventArgs.NewStartingIndex >= 0
                     && eventArgs.OldItems?.Count == 1:
                _items.Move(eventArgs.OldStartingIndex, eventArgs.NewStartingIndex);
                break;
            default:
                RebuildSourcePrefix();
                break;
        }
    }

    private void InsertRange(int index, System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (TItem item in items)
        {
            _items.Insert(index++, item);
        }
    }

    private void RemoveRange(int index, int count)
    {
        for (var removed = 0; removed < count; removed++)
        {
            _items.RemoveAt(index);
        }
    }

    private void ReplaceRange(int index, System.Collections.IList? items)
    {
        if (items is null)
        {
            return;
        }

        foreach (TItem item in items)
        {
            _items[index++] = item;
        }
    }

    private void RebuildSourcePrefix()
    {
        _items.ReplaceAll(_source.Concat(_suffix));
    }

    private void OnDeferredReconciliationCommitting(IReadOnlyList<TItem> replacement)
        => _items.ReconcileWindow(replacement, _suffix);
}

internal sealed class TranscriptObservableCollection<TItem> : ObservableCollection<TItem>
    where TItem : class
{
    private TItem[]? _deferredSnapshot;
    private int _deferDepth;
    private bool _suppressNotifications;

    internal event Action<IReadOnlyList<TItem>>? DeferredReconciliationCommitting;

    internal bool IsPublishingDeferredReconciliation { get; private set; }

    public TranscriptObservableCollection()
    {
    }

    public TranscriptObservableCollection(IEnumerable<TItem> items)
        : base(items)
    {
    }

    public void ReplaceAll(IEnumerable<TItem> items)
    {
        ArgumentNullException.ThrowIfNull(items);
        var replacement = items.ToArray();
        CheckReentrancy();
        Items.Clear();
        foreach (var item in replacement)
        {
            Items.Add(item);
        }

        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Reset));
    }

    internal IDisposable DeferReconciliation()
    {
        if (_deferDepth++ == 0)
        {
            _deferredSnapshot = Items.ToArray();
            _suppressNotifications = true;
        }
        return new DeferredReconciliation(this);
    }

    protected override void OnCollectionChanged(NotifyCollectionChangedEventArgs eventArgs)
    {
        if (!_suppressNotifications)
        {
            base.OnCollectionChanged(eventArgs);
        }
    }

    protected override void OnPropertyChanged(PropertyChangedEventArgs eventArgs)
    {
        if (!_suppressNotifications)
        {
            base.OnPropertyChanged(eventArgs);
        }
    }

    private void EndDeferredReconciliation()
    {
        if (_deferDepth == 0 || --_deferDepth > 0)
        {
            return;
        }

        var snapshot = _deferredSnapshot ?? [];
        var replacement = Items.ToArray();
        Items.Clear();
        foreach (var item in snapshot)
        {
            Items.Add(item);
        }
        _deferredSnapshot = null;
        _suppressNotifications = false;
        IsPublishingDeferredReconciliation = true;
        try
        {
            try
            {
                DeferredReconciliationCommitting?.Invoke(replacement);
            }
            finally
            {
                Reconcile(replacement);
            }
        }
        finally
        {
            IsPublishingDeferredReconciliation = false;
        }
    }

    internal void ReconcileWindow(
        IReadOnlyList<TItem> replacement,
        IReadOnlyList<TItem> permanentSuffix)
    {
        if (permanentSuffix.Count == 0)
        {
            Reconcile(replacement);
            return;
        }

        var currentSourceCount = Count - permanentSuffix.Count;
        if (currentSourceCount < 0
            || !Items.Skip(currentSourceCount)
                .SequenceEqual(permanentSuffix, ReferenceEqualityComparer.Instance))
        {
            ReplaceAll(replacement.Concat(permanentSuffix));
            return;
        }

        var currentSource = Items.Take(currentSourceCount).ToArray();
        var (currentStart, replacementStart, retainedCount) = FindLongestSharedRange(
            currentSource,
            replacement);
        var replacementItems = replacement.ToHashSet(ReferenceEqualityComparer.Instance);
        if (retainedCount == 0
            || currentSource.Count(replacementItems.Contains) != retainedCount)
        {
            ReplaceAll(replacement.Concat(permanentSuffix));
            return;
        }

        // Avalonia's virtualizing layout clears every realized element on Remove.
        // Collapse trimmed edges with Replace so the shared range remains realized.
        var trimmedSuffixCount = currentSource.Length - currentStart - retainedCount;
        if (trimmedSuffixCount > 0)
        {
            ReplaceRange(
                currentStart + retainedCount,
                trimmedSuffixCount + permanentSuffix.Count,
                permanentSuffix);
        }

        if (currentStart > 0)
        {
            ReplaceRange(0, currentStart + 1, [currentSource[currentStart]]);
        }

        if (replacementStart > 0)
        {
            InsertRange(0, replacement.Take(replacementStart).ToArray());
        }

        var appendedCount = replacement.Count - replacementStart - retainedCount;
        if (appendedCount > 0)
        {
            InsertRange(
                replacementStart + retainedCount,
                replacement.Skip(replacementStart + retainedCount).ToArray());
        }

        if (!Items.Cast<TItem>().SequenceEqual(
                replacement.Concat(permanentSuffix),
                ReferenceEqualityComparer.Instance))
        {
            ReplaceAll(replacement.Concat(permanentSuffix));
        }
    }

    private void InsertRange(int index, IReadOnlyList<TItem> items)
    {
        if (items.Count == 0)
        {
            return;
        }

        CheckReentrancy();
        for (var offset = 0; offset < items.Count; offset++)
        {
            Items.Insert(index + offset, items[offset]);
        }
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Add,
            items.ToArray(),
            index));
    }

    private void ReplaceRange(
        int index,
        int oldCount,
        IReadOnlyList<TItem> replacement)
    {
        if (oldCount <= 0 || replacement.Count == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(oldCount));
        }

        CheckReentrancy();
        var removed = Items.Skip(index).Take(oldCount).ToArray();
        for (var offset = 0; offset < oldCount; offset++)
        {
            Items.RemoveAt(index);
        }
        for (var offset = 0; offset < replacement.Count; offset++)
        {
            Items.Insert(index + offset, replacement[offset]);
        }
        if (oldCount != replacement.Count)
        {
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        }
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(
            NotifyCollectionChangedAction.Replace,
            replacement.ToArray(),
            removed,
            index));
    }

    private static (int CurrentStart, int ReplacementStart, int Count) FindLongestSharedRange(
        IReadOnlyList<TItem> current,
        IReadOnlyList<TItem> replacement)
    {
        var bestCurrentStart = 0;
        var bestReplacementStart = 0;
        var bestCount = 0;
        // Transcript rows are unique by reference. Index them once instead of
        // comparing every pair and then rescanning each shared suffix.
        var positions = new Dictionary<TItem, int>(ReferenceEqualityComparer.Instance);
        for (var index = 0; index < current.Count; index++) positions.Add(current[index], index);
        var previous = -2;
        var length = 0;
        for (var index = 0; index < replacement.Count; index++)
        {
            if (!positions.TryGetValue(replacement[index], out var position))
            {
                previous = -2;
                length = 0;
                continue;
            }
            length = position == previous + 1 ? length + 1 : 1;
            previous = position;
            var start = position - length + 1;
            if (length > bestCount || length == bestCount && start < bestCurrentStart)
            {
                bestCurrentStart = start;
                bestReplacementStart = index - length + 1;
                bestCount = length;
            }
        }

        return (bestCurrentStart, bestReplacementStart, bestCount);
    }

    private void Reconcile(IReadOnlyList<TItem> replacement)
    {
        var desiredItems = replacement.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var index = Count - 1; index >= 0; index--)
        {
            if (!desiredItems.Contains(this[index]))
            {
                RemoveAt(index);
            }
        }
        for (var targetIndex = 0; targetIndex < replacement.Count; targetIndex++)
        {
            var item = replacement[targetIndex];
            if (targetIndex < Count && ReferenceEquals(this[targetIndex], item))
            {
                continue;
            }
            var existingIndex = -1;
            for (var candidateIndex = targetIndex; candidateIndex < Count; candidateIndex++)
            {
                if (ReferenceEquals(this[candidateIndex], item))
                {
                    existingIndex = candidateIndex;
                    break;
                }
            }
            if (existingIndex >= 0)
            {
                Move(existingIndex, targetIndex);
            }
            else
            {
                Insert(targetIndex, item);
            }
        }
        while (Count > replacement.Count)
        {
            RemoveAt(Count - 1);
        }
    }

    // Presentation-only regrouping must not issue Reset/Remove notifications:
    // ItemsRepeater otherwise discards every realized anchor, including suffixes.
    internal void ReconcilePresentation(IReadOnlyList<TItem> replacement)
    {
        var desired = replacement.ToHashSet(ReferenceEqualityComparer.Instance);
        var existingItems = this.ToHashSet(ReferenceEqualityComparer.Instance);
        for (var target = 0; target < replacement.Count; target++)
        {
            if (target < Count && ReferenceEquals(this[target], replacement[target])) continue;
            if (!existingItems.Contains(replacement[target]))
            {
                var end = target + 1;
                while (end < replacement.Count && !existingItems.Contains(replacement[end])) end++;
                InsertRange(target, replacement.Skip(target).Take(end - target).ToArray());
                target = end - 1;
                continue;
            }
            var existing = IndexOf(replacement[target]);
            if (existing > target && Items.Skip(target).Take(existing - target).All(item => !desired.Contains(item)))
                ReplaceRange(target, existing - target + 1, [replacement[target]]);
            // ItemsRepeater's ElementManager handles Move, but its ViewManager
            // does not update the corresponding virtualization indices. Replace
            // only the reordered interval so the two maps cannot diverge.
            else if (existing >= 0)
                ReplaceRange(target, existing - target + 1,
                    new[] { replacement[target] }.Concat(Items.Skip(target).Take(existing - target)).ToArray());
        }
        if (Count > replacement.Count && replacement.Count > 0)
            ReplaceRange(replacement.Count - 1, Count - replacement.Count + 1, [replacement[^1]]);
        else if (replacement.Count == 0)
            while (Count > 0) RemoveAt(Count - 1);
    }

    // Member chunks normally append within one group. Keep the rest of the
    // transcript and permanent suffixes out of the publication/reconciliation.
    internal void ReconcileSlice(int start, int oldCount, IReadOnlyList<TItem> replacement)
    {
        var prefix = 0;
        while (prefix < oldCount && prefix < replacement.Count
               && ReferenceEquals(this[start + prefix], replacement[prefix])) prefix++;
        var suffix = 0;
        while (suffix < oldCount - prefix && suffix < replacement.Count - prefix
               && ReferenceEquals(this[start + oldCount - suffix - 1], replacement[replacement.Count - suffix - 1])) suffix++;
        var removed = oldCount - prefix - suffix;
        var added = replacement.Count - prefix - suffix;
        var index = start + prefix;
        if (removed == 0)
        {
            InsertRange(index, replacement.Skip(prefix).Take(added).ToArray());
        }
        else if (added > 0)
        {
            ReplaceRange(index, removed, replacement.Skip(prefix).Take(added).ToArray());
        }
        else if (index + removed < Count)
        {
            ReplaceRange(index, removed + 1, [this[index + removed]]);
        }
        else if (index > 0)
        {
            ReplaceRange(index - 1, removed + 1, [this[index - 1]]);
        }
        else ReplaceAll([]);
    }

    private sealed class DeferredReconciliation(TranscriptObservableCollection<TItem> owner) : IDisposable
    {
        private TranscriptObservableCollection<TItem>? _owner = owner;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.EndDeferredReconciliation();
    }
}
