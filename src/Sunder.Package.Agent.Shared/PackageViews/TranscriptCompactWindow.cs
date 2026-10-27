using System.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

// Owns top-level entries independently of the flat, virtualized display list.
// Members exist only while a group is open; they never spend the outer row budget.
internal sealed class TranscriptCompactWindow<TRow> : IDisposable where TRow : class
{
    private readonly TranscriptRowProjector<TRow> _projector;
    private readonly Func<AgentTranscriptActivityMembersRequest, CancellationToken, Task<AgentTranscriptActivityMembersPage>> _loadMembers;
    private readonly Func<Action, CancellationToken, Task> _dispatch;
    private readonly PresentationTaskScope _tasks = new(PresentationTaskScope.IgnoreExpectedFailure);
    private readonly List<Entry> _entries = [];
    private long _epoch;

    internal TranscriptCompactWindow(TranscriptRowProjector<TRow> projector,
        Func<AgentTranscriptActivityMembersRequest, CancellationToken, Task<AgentTranscriptActivityMembersPage>> loadMembers,
        Func<Action, CancellationToken, Task> dispatch)
    {
        _projector = projector;
        _loadMembers = loadMembers;
        _dispatch = dispatch;
    }

    internal event Action? Changed;
    internal IReadOnlyList<AgentCompactTranscriptEntry> Entries => _entries.Select(entry => entry.Record).ToArray();
    internal bool HasRunningTools => _entries.Any(entry => entry.Record.Group?.RunningCount > 0);
    internal TranscriptPageCursor? Oldest => Position(_entries.FirstOrDefault()?.Record);
    internal TranscriptPageCursor? Newest => Position(_entries.LastOrDefault()?.Record);
    internal Task PendingLoads => Task.WhenAll(_entries.Select(entry => entry.LoadTask));

    internal bool Matches(AgentCompactTranscriptPage page)
        => page.Entries.Count == _entries.Count && page.Entries.Zip(_entries).All(pair =>
            pair.First.EntryId == pair.Second.Record.EntryId
            && (pair.First.Group is { } group && pair.Second.Record.Group is { } current
                ? group.TranscriptEpoch == current.TranscriptEpoch && group.Revision == current.Revision
                    && group.Last.ItemId == current.Last.ItemId
                    && group.RunningCount == current.RunningCount && group.FailedCount == current.FailedCount
                    && group.CompletedCount == current.CompletedCount
                    && group.Tools.Select(tool => (tool.ToolId, tool.Count)).SequenceEqual(current.Tools.Select(tool => (tool.ToolId, tool.Count)))
                : pair.First.Message is { } message && pair.Second.Record.Message is { } existing
                    && message.ContentRevision == existing.ContentRevision && message.UpdatedAtUtc == existing.UpdatedAtUtc));

    internal TranscriptTrimResult Apply(AgentCompactTranscriptPage page, bool replace, bool older, int limit, object? protectedAnchor, bool targeted = false)
    {
        if (page.TranscriptEpoch > 0) _epoch = page.TranscriptEpoch;
        var incomingIds = page.Entries.Select(entry => entry.EntryId).ToHashSet();
        if (replace)
            foreach (var entry in _entries.Where(entry => !incomingIds.Contains(entry.Record.EntryId)).ToArray()) Remove(entry);
        foreach (var record in page.Entries)
        {
            var existing = _entries.FirstOrDefault(entry => entry.Record.EntryId == record.EntryId);
            if (existing is null)
            {
                if (record.Group is { } group)
                {
                    var state = new TranscriptActivityGroupState(new(new TranscriptCompactGroupKey(group.SessionId, group.First.ItemId)));
                    state.UpdateSummary(group);
                    existing = new(record, _projector.CreateCompactGroup(group, state), state);
                    state.PropertyChanged += OnGroupChanged;
                }
                else if (record.Message is { } message && _projector.CreateCompactMessage(message) is { } row)
                    existing = new(record, row, null);
                else continue;
                _entries.Add(existing);
            }
            else if (record.Group is { } summary)
            {
                if (existing.Record.Group is { } current && current.TranscriptEpoch == summary.TranscriptEpoch && current.Revision > summary.Revision) continue;
                var epochChanged = existing.Record.Group?.TranscriptEpoch != summary.TranscriptEpoch;
                var revalidate = !targeted && existing.LoadedRevision > 0 && existing.LoadedRevision < summary.Revision;
                existing.Record = record;
                var state = existing.State!;
                _projector.UpdateCompactGroup(state, summary);
                if (epochChanged) InvalidateMembers(existing);
                if (revalidate)
                {
                    existing.ThroughItemId = null;
                    if (existing.LoadCancellation is not null) existing.NeedsRevalidation = true;
                }
                if (state.IsExpanded && existing.LoadCancellation is null
                    && (epochChanged || revalidate || existing.ThroughItemId != summary.Last.ItemId)) StartLoading(existing);
            }
            else if (record.Message is { } message
                     && (existing.Record.Message is not { } current || current.ContentRevision < message.ContentRevision
                         || current.ContentRevision == message.ContentRevision && AgentTranscriptTime.Read(current.UpdatedAtUtc) <= AgentTranscriptTime.Read(message.UpdatedAtUtc)))
            {
                existing.Record = record;
                _projector.UpdateCompactMessage(existing.Row, message);
            }
        }
        _entries.Sort((left, right) => Compare(left.Record, right.Record));
        var protectedEntry = FindOwner(protectedAnchor);
        var retained = TranscriptRowWindow.SelectRetainedRows(_entries.ToArray(), limit,
            older ? AgentTranscriptTrimDirection.Newest : AgentTranscriptTrimDirection.Oldest,
            protectedEntry, entry => entry);
        var first = retained.FirstOrDefault();
        var last = retained.LastOrDefault();
        var trimmed = TranscriptTrimResult.None;
        if (_entries.Count > 0 && first != _entries[0]) trimmed |= TranscriptTrimResult.Oldest;
        if (_entries.Count > 0 && last != _entries[^1]) trimmed |= TranscriptTrimResult.Newest;
        var retainedSet = retained.ToHashSet();
        foreach (var entry in _entries.Where(entry => !retainedSet.Contains(entry)).ToArray()) Remove(entry);
        Publish();
        return trimmed;
    }

    internal bool ApplyLiveGroup(AgentTranscriptActivityGroup group, AgentTurnRecord turn, bool following, int limit, object? protectedAnchor)
    {
        if (_epoch > 0 && _epoch != group.TranscriptEpoch) return false;
        var existing = _entries.FirstOrDefault(entry => entry.Record.EntryId == group.First.ItemId);
        if (existing is null && (!following || _entries.LastOrDefault() is { } last
            && last.Record.TurnSequence > group.First.TurnSequence)) return true;
        var record = GroupEntry(group);
        Apply(GroupPage(record, group.TranscriptEpoch), replace: false, older: false, limit, protectedAnchor, targeted: true);
        existing = _entries.FirstOrDefault(entry => entry.Record.EntryId == group.First.ItemId);
        if (existing?.LoadCancellation is not null)
        {
            // A result can arrive between a member-page read and publication.
            // Retain only in-flight header updates, not tool payloads or history.
            foreach (var item in turn.Items.Where(item => item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult))
            {
                var key = TranscriptRowAnchorKey.Tool(turn, item);
                if (!existing.PendingHeaders.ContainsKey(key) && existing.PendingHeaders.Count >= 512)
                {
                    existing.PendingHeaders.Clear();
                    existing.NeedsRevalidation = true;
                }
                var header = turn with { Items = [item] };
                if (!existing.PendingHeaders.TryGetValue(key, out var previous) || Revision(header) >= Revision(previous))
                    existing.PendingHeaders[key] = header;
            }
        }
        return true;
    }

    private static AgentCompactTranscriptEntry GroupEntry(AgentTranscriptActivityGroup group)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { EntryId = group.First.ItemId, TurnId = group.First.TurnId, CreatedAtUtc = group.First.CreatedAtUtc,
            TurnSequence = group.First.TurnSequence, Group = group };
#else
        => new(group.First.ItemId, group.First.CreatedAtUtc, group.First.TurnId, null, group) { TurnSequence = group.First.TurnSequence };
#endif

    private static AgentCompactTranscriptPage GroupPage(AgentCompactTranscriptEntry entry, long epoch)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { Entries = [entry], HasOlder = false, HasNewer = false, TranscriptEpoch = epoch };
#else
        => new([entry], false, false) { TranscriptEpoch = epoch };
#endif

    internal bool ApplyLiveTurn(AgentTurnRecord turn)
    {
        var changed = false;
        var backing = new List<(TRow, AgentTurnRecord)>();
        foreach (var entry in _entries)
        {
            if (entry.Record.Message is { } current && current.TurnId == turn.TurnId
                && (current.ContentRevision < turn.ContentRevision || current.ContentRevision == turn.ContentRevision
                    && AgentTranscriptTime.Read(current.UpdatedAtUtc) <= AgentTranscriptTime.Read(turn.UpdatedAtUtc)))
            {
                entry.Record = WithMessage(entry.Record, turn);
                _projector.UpdateCompactMessage(entry.Row, turn);
                backing.Add((entry.Row, turn));
                changed = true;
            }
            foreach (var item in turn.Items.Where(item => item.Kind is AgentTurnItemKind.ToolCall or AgentTurnItemKind.ToolResult))
            {
                var anchor = TranscriptRowAnchorKey.Tool(turn, item);
                if (!entry.MemberIndices.TryGetValue(anchor, out var index)) continue;
                var single = turn with { Items = [item] };
                var member = entry.Members[index];
                if (Revision(single) < Math.Max(Revision(member.Turn), member.Observation is { } observation ? Revision(observation) : 0)) continue;
                _projector.UpdateCompactTool(member.Row, single);
                entry.Members[index] = (member.Row, member.Turn, single);
                backing.Add((member.Row, member.Turn));
                backing.Add((member.Row, single));
                // Keep the canonical invocation position even when a later result updates its header.
                changed = true;
            }
        }
        if (changed)
        {
            _projector.UpdateCompactBacking(backing);
            Changed?.Invoke();
        }
        return changed;
    }

    internal bool TryAppendLiveMessage(AgentTurnRecord turn, int limit, out bool trimmedOldest)
    {
        trimmedOldest = false;
        if (turn.Kind is AgentTurnKind.ToolCall or AgentTurnKind.ToolResult
            || _projector.CreateCompactMessage(turn) is not { } row) return false;
        _entries.Add(new(MessageEntry(turn), row, null));
        _entries.Sort((left, right) => Compare(left.Record, right.Record));
        while (_entries.Count > limit)
        {
            Remove(_entries[0]);
            trimmedOldest = true;
        }
        Publish();
        return true;
    }

    internal object ResolveVisibleAnchor(object anchor)
        => FindOwner(anchor) is { State: { IsExpanded: false } state } ? state.Anchor : anchor;

    internal TranscriptModeAnchor? CaptureModeAnchor(object? anchor, TranscriptViewportAnchorData? viewport)
    {
        var owner = FindOwner(anchor);
        if (owner is null) return null;
        var member = owner.Members.FirstOrDefault(member => Equals(_projector.GetAnchorKey(member.Row), anchor));
        if (member.Turn is { } turn)
            return new(turn.TurnId, turn.Items.Single().ItemId, AgentTranscriptTime.Read(turn.CreatedAtUtc), anchor!, viewport);
        if (owner.Record.Group is { } group)
            return new(group.First.TurnId, group.First.ItemId, AgentTranscriptTime.Read(group.First.CreatedAtUtc), anchor!, viewport);
        return new(owner.Record.TurnId, null, AgentTranscriptTime.Read(owner.Record.CreatedAtUtc), anchor!, viewport);
    }

    internal object? AnchorForEntry(Guid id)
        => _entries.FirstOrDefault(entry => entry.Record.EntryId == id) is { } entry ? _projector.GetAnchorKey(entry.Row) : null;

    internal async Task RevealEntryAsync(Guid entryId, CancellationToken cancellationToken)
    {
        var entry = _entries.FirstOrDefault(entry => entry.Record.EntryId == entryId);
        if (entry?.State is not { } state) return;
        state.IsExpanded = true;
        await entry.LoadTask.WaitAsync(cancellationToken);
        if (state.FailureText.Length > 0) throw new InvalidOperationException(state.FailureText);
    }

    internal void Reset()
    {
        foreach (var entry in _entries.ToArray()) Remove(entry);
        _epoch = 0;
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName != nameof(TranscriptActivityGroupState.IsExpanded)) return;
        var entry = _entries.FirstOrDefault(entry => ReferenceEquals(entry.State, sender));
        if (entry is null) return;
        if (entry.State!.IsExpanded) StartLoading(entry);
        else { ClearMembers(entry); Publish(); }
    }

    private void StartLoading(Entry entry)
    {
        if (entry.LoadCancellation is not null || entry.Record.Group is not { } group) return;
        var cancellation = new CancellationTokenSource();
        entry.LoadCancellation = cancellation;
        entry.State!.SetLoading(true);
        entry.LoadTask = _tasks.Track(_ => LoadMembersAsync(entry, group, cancellation));
    }

    private async Task LoadMembersAsync(Entry entry, AgentTranscriptActivityGroup snapshot, CancellationTokenSource cancellation)
    {
        var token = cancellation.Token;
        AgentTranscriptInvocationCursor? after = entry.ThroughItemId is null || entry.LoadedEpoch != snapshot.TranscriptEpoch ? null
            : entry.Members.LastOrDefault() is { } last ? TranscriptCompactPayload.Cursor(last.Turn, last.Turn.Items.Single()) : null;
        var reload = after is null;
        var revalidating = reload && entry.Members.Count > 0;
        var epochChanged = entry.LoadedEpoch != snapshot.TranscriptEpoch;
        var seen = new HashSet<object>();
        try
        {
            await Task.Yield();
            while (true)
            {
                token.ThrowIfCancellationRequested();
                var page = await _loadMembers(MembersRequest(snapshot, after), token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                TranscriptCompactValidation.Members(page, snapshot, after);
                await _dispatch(() =>
                {
                    if (!IsCurrent(entry, cancellation)) return;
                    var backing = new List<(TRow, AgentTurnRecord)>();
                    foreach (var header in page.Headers)
                    {
                        var anchor = TranscriptRowAnchorKey.Tool(header, header.Items.Single());
                        seen.Add(anchor);
                        if (entry.MemberIndices.TryGetValue(anchor, out var index))
                        {
                            var member = entry.Members[index];
                            if (!epochChanged && Revision(header) < Revision(member.Turn)
                                || member.Observation is { } observation && Revision(header) < Revision(observation)) continue;
                            _projector.UpdateCompactTool(member.Row, header);
                            entry.Members[index] = (member.Row, header, null);
                        }
                        else
                        {
                            entry.MemberIndices.Add(anchor, entry.Members.Count);
                            entry.Members.Add((_projector.CreateCompactTool(header), header, null));
                            entry.MemberAnchors.Add(anchor);
                        }
                        if (entry.PendingHeaders.Remove(anchor, out var live)
                            && Revision(live) >= Revision(header))
                        {
                            var current = entry.Members[entry.MemberIndices[anchor]];
                            _projector.UpdateCompactTool(current.Row, live);
                            entry.Members[entry.MemberIndices[anchor]] = (current.Row, header, live);
                        }
                        var published = entry.Members[entry.MemberIndices[anchor]];
                        backing.Add((published.Row, header));
                        if (published.Observation is { } latest) backing.Add((published.Row, latest));
                    }
                    if (backing.Count > 0) PublishMembers(entry, backing);
                }, token).ConfigureAwait(false);
                if (!page.HasMore) break;
                after = page.Continuation;
            }
            await _dispatch(() =>
            {
                if (!IsCurrent(entry, cancellation)) return;
                if (reload)
                {
                    var removed = entry.Members.RemoveAll(member => !seen.Contains(_projector.GetAnchorKey(member.Row)));
                    entry.MemberAnchors.IntersectWith(seen);
                    var reordered = false;
                    if (revalidating)
                    {
                        // Cache one cursor per member, not two allocations for
                        // every sort comparison during an epoch replacement.
                        var ordered = entry.Members.OrderBy(member => TranscriptCompactPayload.Cursor(member.Turn, member.Turn.Items.Single()),
                            Comparer<AgentTranscriptInvocationCursor>.Create(TranscriptCompactValidation.Compare)).ToArray();
                        reordered = !ordered.Select(member => member.Row).SequenceEqual(entry.Members.Select(member => member.Row), ReferenceEqualityComparer.Instance);
                        if (reordered) { entry.Members.Clear(); entry.Members.AddRange(ordered); }
                    }
                    if (removed > 0 || reordered)
                    {
                        entry.MemberIndices.Clear();
                        for (var index = 0; index < entry.Members.Count; index++)
                            entry.MemberIndices.Add(_projector.GetAnchorKey(entry.Members[index].Row), index);
                        PublishMembers(entry, []);
                    }
                }
                entry.ThroughItemId = snapshot.Last.ItemId;
                entry.LoadedRevision = snapshot.Revision;
                entry.LoadedEpoch = snapshot.TranscriptEpoch;
            }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (PresentationExceptionPolicy.IsExpectedOperationFailure(exception))
        {
            await _dispatch(() =>
            {
                if (IsCurrent(entry, cancellation)) entry.State!.SetFailure(exception.Message);
            }, CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            await _dispatch(() =>
            {
                if (!ReferenceEquals(entry.LoadCancellation, cancellation)) return;
                entry.LoadCancellation = null;
                entry.State!.SetLoading(false);
                cancellation.Dispose();
                if (entry.NeedsRevalidation)
                {
                    entry.NeedsRevalidation = false;
                    entry.ThroughItemId = null;
                }
                if (entry.State.IsExpanded && entry.State.FailureText.Length == 0
                    && entry.Record.Group is { } current
                    && (entry.ThroughItemId is null || current.Last.ItemId != snapshot.Last.ItemId)) StartLoading(entry);
                else entry.PendingHeaders.Clear();
            }, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private bool IsCurrent(Entry entry, CancellationTokenSource cancellation)
        => !cancellation.IsCancellationRequested && ReferenceEquals(entry.LoadCancellation, cancellation) && _entries.Contains(entry);

    private static void CancelMemberRead(Entry entry)
    {
        var cancellation = entry.LoadCancellation;
        entry.LoadCancellation = null;
        cancellation?.Cancel();
        cancellation?.Dispose();
        entry.State?.SetLoading(false);
    }

    private static void InvalidateMembers(Entry entry)
    {
        CancelMemberRead(entry);
        entry.PendingHeaders.Clear();
        entry.NeedsRevalidation = false;
        entry.ThroughItemId = null;
        entry.LoadedRevision = 0;
        // A text edit advances the session epoch too. Keep visible VMs until the
        // authoritative header snapshot identifies which invocations still exist.
        for (var index = 0; index < entry.Members.Count; index++)
        {
            var member = entry.Members[index];
            entry.Members[index] = (member.Row, member.Turn, null);
        }
    }

    private static void ClearMembers(Entry entry, bool forgetAnchors = false)
    {
        CancelMemberRead(entry);
        entry.Members.Clear();
        entry.MemberIndices.Clear();
        if (forgetAnchors) entry.MemberAnchors.Clear();
        entry.ThroughItemId = null;
        entry.LoadedRevision = 0;
        entry.LoadedEpoch = 0;
        entry.PendingHeaders.Clear();
        entry.NeedsRevalidation = false;
    }

    private void Remove(Entry entry)
    {
        ClearMembers(entry, forgetAnchors: true);
        if (entry.State is { } state) state.PropertyChanged -= OnGroupChanged;
        _entries.Remove(entry);
    }

    private Entry? FindOwner(object? anchor)
        => anchor is null ? null : _entries.FirstOrDefault(entry => Equals(_projector.GetAnchorKey(entry.Row), anchor)
            || anchor is TranscriptActivityGroupAnchor { SourceAnchor: TranscriptCompactGroupKey key }
                && entry.Record.Group is { } group && group.First.ItemId == key.FirstItemId && group.SessionId == key.SessionId
            || entry.MemberAnchors.Contains(anchor));

    private void Publish()
    {
        var rows = new List<TRow>();
        var backing = new List<(TRow, AgentTurnRecord)>();
        foreach (var entry in _entries)
        {
            rows.Add(entry.Row);
            if (entry.Record.Message is { } message) backing.Add((entry.Row, message));
            entry.PublishedMemberCount = entry.State?.IsExpanded == true ? entry.Members.Count : 0;
            if (entry.State?.IsExpanded != true) continue;
            rows.AddRange(entry.Members.Select(member => member.Row));
            backing.AddRange(entry.Members.Select(member => (member.Row, member.Turn)));
            backing.AddRange(entry.Members.Where(member => member.Observation is not null).Select(member => (member.Row, member.Observation!)));
        }
        _projector.PublishCompactRows(rows, backing);
        Changed?.Invoke();
    }

    private void PublishMembers(Entry entry, IReadOnlyList<(TRow Row, AgentTurnRecord Turn)> backing)
    {
        var rows = entry.Members.Select(member => member.Row).ToArray();
        _projector.PublishCompactMembers(entry.Row, entry.PublishedMemberCount, rows, backing);
        entry.PublishedMemberCount = rows.Length;
        Changed?.Invoke();
    }

    private static int Compare(AgentCompactTranscriptEntry left, AgentCompactTranscriptEntry right)
    {
        var order = left.TurnSequence.CompareTo(right.TurnSequence);
        if (order != 0) return order;
        var comparison = AgentTranscriptTime.Read(left.CreatedAtUtc).CompareTo(AgentTranscriptTime.Read(right.CreatedAtUtc));
        return comparison != 0 ? comparison : string.CompareOrdinal(left.TurnId.ToString("D"), right.TurnId.ToString("D"));
    }

    private static TranscriptPageCursor? Position(AgentCompactTranscriptEntry? entry)
        => entry is null ? null : new(AgentTranscriptTime.Read(entry.CreatedAtUtc), entry.TurnId) { TurnSequence = entry.TurnSequence };

    private static long Revision(AgentTurnRecord header)
        => header.Items.Single().ToolDetailRevision;

    private static AgentCompactTranscriptEntry WithMessage(AgentCompactTranscriptEntry entry, AgentTurnRecord turn)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { EntryId = entry.EntryId, CreatedAtUtc = entry.CreatedAtUtc, TurnId = entry.TurnId, Group = entry.Group, Message = turn, TurnSequence = entry.TurnSequence };
#else
        => entry with { Message = turn };
#endif

    private static AgentCompactTranscriptEntry MessageEntry(AgentTurnRecord turn)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { EntryId = turn.TurnId, CreatedAtUtc = turn.CreatedAtUtc, TurnId = turn.TurnId, Message = turn, TurnSequence = turn.TurnSequence };
#else
        => new(turn.TurnId, turn.CreatedAtUtc, turn.TurnId, turn, null) { TurnSequence = turn.TurnSequence };
#endif

    private static AgentTranscriptActivityMembersRequest MembersRequest(AgentTranscriptActivityGroup group, AgentTranscriptInvocationCursor? after)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { SessionId = group.SessionId, FirstItemId = group.First.ItemId, TranscriptEpoch = group.TranscriptEpoch, Through = group.Last, After = after, Limit = 100 };
#else
        => new(group.SessionId, group.First.ItemId, group.TranscriptEpoch, group.Last, after);
#endif

    public void Dispose() { Reset(); _tasks.Dispose(); }

    private sealed class Entry(AgentCompactTranscriptEntry record, TRow row, TranscriptActivityGroupState? state)
    {
        internal readonly Dictionary<object, AgentTurnRecord> PendingHeaders = [];
        internal bool NeedsRevalidation;
        internal AgentCompactTranscriptEntry Record = record;
        internal TRow Row { get; } = row;
        internal TranscriptActivityGroupState? State { get; } = state;
        internal List<(TRow Row, AgentTurnRecord Turn, AgentTurnRecord? Observation)> Members { get; } = [];
        internal Dictionary<object, int> MemberIndices { get; } = [];
        internal int PublishedMemberCount;
        // Only lightweight keys survive collapse, so a viewport inside a removed
        // member can resolve to its parent without retaining headers or detail VMs.
        internal HashSet<object> MemberAnchors { get; } = [];
        internal Guid? ThroughItemId;
        internal long LoadedRevision;
        internal long LoadedEpoch;
        internal CancellationTokenSource? LoadCancellation;
        internal Task LoadTask = Task.CompletedTask;
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed record TranscriptCompactGroupKey(Guid SessionId, Guid FirstItemId);
