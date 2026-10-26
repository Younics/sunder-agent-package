using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Shared.PackageViews;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed record TranscriptActivityGroupAnchor(object SourceAnchor);
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed record TranscriptActivityMember(object Anchor, object Scope, string ToolId, string Status, bool IsRunning)
{
    public string? OperationReference { get; init; }
    public string? OperationStatus { get; init; }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal static class TranscriptActivityGrouping
{
    internal static TranscriptActivityMember Describe(TranscriptToolProjection tool)
        => new(tool.AnchorKey, (tool.SessionId, tool.RunId, tool.RunRevision), tool.ToolId, tool.StatusText, tool.IsRunning)
        { OperationReference = tool.OperationReference, OperationStatus = tool.OperationStatus };

    internal static (string Singular, string Plural) Category(string toolId)
        => toolId.ToLowerInvariant() switch
        {
            "read" => ("Read", "Reads"),
            "grep" => ("Search", "Searches"),
            "glob" => ("Glob", "Globs"),
            "shell" => ("Shell", "Shells"),
            "write" => ("Write", "Writes"),
            "edit" => ("Edit", "Edits"),
            "apply_patch" => ("Patch", "Patches"),
            "web_search" => ("Web Search", "Web Searches"),
            "web_fetch" => ("Web Fetch", "Web Fetches"),
            "skill" => ("Skill", "Skills"),
            "skill_resource" => ("Skill Resource", "Skill Resources"),
            "task" or "delegate_tasks" => ("Subagent Task", "Subagent Tasks"),
            _ when McpToolCardPresentation.TryParseStableToolId(toolId, out _, out _) => ("MCP Tool", "MCP Tools"),
            _ => ($"{McpToolCardPresentation.HumanizeToolName(toolId)} Call", $"{McpToolCardPresentation.HumanizeToolName(toolId)} Calls"),
        };
}

// A bounded, view-only projection. Original rows, correlation keys, paging and
// detail loading remain owned by the existing timeline; no history is rewritten.
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class TranscriptActivityProjection<TRow> : IDisposable where TRow : class
{
    private readonly ObservableCollection<TRow> _source;
    private readonly TranscriptObservableCollection<TRow>? _reconcilingSource;
    private readonly Func<TRow, TranscriptActivityMember?> _describe;
    private readonly Func<TRow, TranscriptActivityGroupState, TRow> _createGroup;
    private readonly TranscriptObservableCollection<TRow> _items = [];
    private readonly TRow[] _suffix;
    private readonly HashSet<INotifyPropertyChanged> _subscriptions = [];
    private readonly Dictionary<object, Group> _memberships = [];
    private readonly HashSet<Group> _groups = [];
    private IReadOnlyList<TRow> _current = [];
    private bool _detailed;
    private bool _rebuilding;
    private bool _disposed;

    public TranscriptActivityProjection(ObservableCollection<TRow> source,
        Func<TRow, TranscriptActivityMember?> describe,
        Func<TRow, TranscriptActivityGroupState, TRow> createGroup,
        params TRow[] suffix)
    {
        _source = source;
        _describe = describe;
        _createGroup = createGroup;
        _suffix = suffix;
        Items = new(_items);
        _reconcilingSource = source as TranscriptObservableCollection<TRow>;
        if (_reconcilingSource is not null) _reconcilingSource.DeferredReconciliationCommitting += SetSource;
        _source.CollectionChanged += OnSourceChanged;
        SetSource(source.ToArray());
    }

    public ReadOnlyObservableCollection<TRow> Items { get; }
    internal Func<object?>? InitialNavigationAnchor { get; init; }

    public void SetDetailed(bool value)
    {
        if (_detailed == value) return;
        _detailed = value;
        Rebuild();
    }

    public object RevealAnchor(object anchor)
    {
        if (anchor is TranscriptActivityGroupAnchor groupAnchor)
            return !_detailed && _memberships.TryGetValue(groupAnchor.SourceAnchor, out var currentGroup)
                ? currentGroup.State.Anchor : groupAnchor.SourceAnchor;
        if (!_detailed && _memberships.TryGetValue(anchor, out var group)) group.State.IsExpanded = true;
        return anchor;
    }

    public object ResolveVisibleAnchor(object anchor)
    {
        var source = anchor is TranscriptActivityGroupAnchor groupAnchor ? groupAnchor.SourceAnchor : anchor;
        return !_detailed && _memberships.TryGetValue(source, out var group)
                         && (anchor is TranscriptActivityGroupAnchor || !group.State.IsExpanded)
            ? group.State.Anchor : source;
    }

    private void OnSourceChanged(object? sender, NotifyCollectionChangedEventArgs args)
    {
        if (_reconcilingSource?.IsPublishingDeferredReconciliation != true) SetSource(_source.ToArray());
    }

    private void SetSource(IReadOnlyList<TRow> rows)
    {
        var incoming = rows.ToHashSet(ReferenceEqualityComparer.Instance);
        var replaceWindow = _current.Count == 0 || !_current.Any(incoming.Contains);
        var current = rows.OfType<INotifyPropertyChanged>().ToHashSet();
        foreach (var old in _subscriptions.Except(current).ToArray())
        {
            old.PropertyChanged -= OnRowChanged;
            _subscriptions.Remove(old);
        }
        foreach (var added in current.Except(_subscriptions))
        {
            added.PropertyChanged += OnRowChanged;
            _subscriptions.Add(added);
        }
        _current = rows;
        Rebuild(replaceWindow);
    }

    private void OnRowChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is "StatusText" or "HasChildSessionLinks") Rebuild();
    }

    private void Rebuild(bool replaceWindow = false)
    {
        if (_disposed || _rebuilding) return;
        _rebuilding = true;
        try
        {
            var projected = new List<TRow>();
            var navigationAnchor = replaceWindow ? InitialNavigationAnchor?.Invoke() : null;
            var retained = new HashSet<Group>();
            var memberships = new Dictionary<object, Group>();
            for (var index = 0; index < _current.Count;)
            {
                if (_describe(_current[index]) is not { } first)
                {
                    projected.Add(_current[index++]);
                    continue;
                }
                var firstRow = _current[index];
                var rows = new List<TRow>();
                var members = new List<TranscriptActivityMember>();
                while (index < _current.Count && _describe(_current[index]) is { } member && Equals(first.Scope, member.Scope))
                {
                    rows.Add(_current[index++]);
                    members.Add(member);
                }
                var anchors = members.Select(member => member.Anchor).ToHashSet();
                var group = members.Select(member => _memberships.GetValueOrDefault(member.Anchor))
                    .FirstOrDefault(candidate => candidate is not null && !retained.Contains(candidate)
                        && anchors.Contains(candidate.State.Anchor.SourceAnchor));
                if (group is null)
                {
                    var state = new TranscriptActivityGroupState(new(first.Anchor));
                    group = new(_createGroup(firstRow, state), state);
                    state.PropertyChanged += OnGroupChanged;
                    _groups.Add(group);
                }
                retained.Add(group);
                group.State.Update(members);
                // Anchored window replacement must publish its already-revealed
                // target once, not reset a collapsed group and then insert its rows.
                if (navigationAnchor is not null && anchors.Contains(navigationAnchor)) group.State.IsExpanded = true;
                foreach (var member in members) memberships[member.Anchor] = group;
                if (_detailed) projected.AddRange(rows);
                else
                {
                    projected.Add(group.Row);
                    if (group.State.IsExpanded) projected.AddRange(rows);
                }
            }
            _memberships.Clear();
            foreach (var entry in memberships) _memberships.Add(entry.Key, entry.Value);
            // A wholly new transcript window publishes once, as before. Active
            // window regrouping preserves realized rows and permanent suffixes.
            if (replaceWindow) _items.ReplaceAll(projected.Concat(_suffix));
            else _items.ReconcilePresentation(projected.Concat(_suffix).ToArray());
            foreach (var old in _groups.Except(retained).ToArray())
            {
                old.State.PropertyChanged -= OnGroupChanged;
                (old.Row as IDisposable)?.Dispose();
                _groups.Remove(old);
            }
        }
        finally { _rebuilding = false; }
    }

    private void OnGroupChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName == nameof(TranscriptActivityGroupState.IsExpanded)) Rebuild();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _source.CollectionChanged -= OnSourceChanged;
        if (_reconcilingSource is not null) _reconcilingSource.DeferredReconciliationCommitting -= SetSource;
        foreach (var row in _subscriptions) row.PropertyChanged -= OnRowChanged;
        foreach (var group in _groups)
        {
            group.State.PropertyChanged -= OnGroupChanged;
            (group.Row as IDisposable)?.Dispose();
        }
        _subscriptions.Clear();
        _groups.Clear();
        _memberships.Clear();
    }

    private sealed record Group(TRow Row, TranscriptActivityGroupState State);
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class TranscriptActivityGroupState(TranscriptActivityGroupAnchor anchor) : INotifyPropertyChanged
{
    private bool _expanded;
    public event PropertyChangedEventHandler? PropertyChanged;
    public TranscriptActivityGroupAnchor Anchor { get; } = anchor;
    public string Summary { get; private set; } = "Activity";
    public string StatusText { get; private set; } = string.Empty;
    public bool IsRunning { get; private set; }
    public string ExpandGlyph => IsExpanded ? "▾" : "▸";
    public string AccessibilityText => $"{Summary}. {(IsRunning ? "Processing. " : string.Empty)}{(StatusText.Length > 0 ? StatusText + ". " : string.Empty)}Activity {(IsExpanded ? "expanded" : "collapsed")}. Expand individual calls separately for details.";
    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            Notify(nameof(IsExpanded));
            Notify(nameof(ExpandGlyph));
            Notify(nameof(AccessibilityText));
        }
    }

    public void Update(IReadOnlyList<TranscriptActivityMember> members)
    {
        var summary = string.Join(" · ", members.GroupBy(member => TranscriptActivityGrouping.Category(member.ToolId))
            .Select(group => $"{group.Count()} {(group.Count() == 1 ? group.Key.Singular : group.Key.Plural)}"));
        var running = members.Any(member => member.IsRunning);
        // Calls are counted above; outcomes are counted once per original operation,
        // not once for its mutation and again for each read-only status check.
        var outcomes = members.GroupBy(member => member.OperationReference is { } reference ? (object)reference : member.Anchor)
            .Select(group => group.Last().OperationStatus ?? group.Last().Status).ToArray();
        var failed = outcomes.Count(status => status == "Failed");
        var uncertain = outcomes.Count(status => status is "Ambiguous" or "Unconfirmed");
        var cancelled = outcomes.Count(status => status is "Cancelled" or "Canceled");
        var unverified = outcomes.Count(status => status == "Running (last observed)");
        var status = string.Join(" · ", new[]
        {
            failed > 0 ? $"{failed} Failed" : null,
            uncertain > 0 ? $"{uncertain} Uncertain" : null,
            cancelled > 0 ? $"{cancelled} Cancelled" : null,
            unverified > 0 ? $"{unverified} Unverified" : null,
        }.OfType<string>());
        if (Summary != summary) { Summary = summary; Notify(nameof(Summary)); }
        if (StatusText != status) { StatusText = status; Notify(nameof(StatusText)); }
        if (IsRunning != running) { IsRunning = running; Notify(nameof(IsRunning)); }
        Notify(nameof(AccessibilityText));
    }

    private void Notify(string name) => PropertyChanged?.Invoke(this, new(name));
}
