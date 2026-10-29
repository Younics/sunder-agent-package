using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed record CapabilityGroupDefinition(
    string Key,
    string Title,
    string? Description,
    int SortOrder,
    bool AllowsBulkSelection = false);

internal sealed record CapabilityOptionDefinition(
    string Category,
    string Kind,
    string CapabilityId,
    string? SourceId,
    string DisplayName,
    string? Description,
    string StatusText,
    bool CanSelect,
    CapabilityGroupDefinition Group,
    IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord>? Aliases = null,
    bool AllowUnscopedAssignment = false);

internal sealed class CapabilityGroupState(
    string category,
    string key,
    Action<CapabilityGroupState, bool> setEnabled) : INotifyPropertyChanged, IDisposable
{
    private CapabilityGroupDefinition _definition = new(key, string.Empty, null, 0);
    public event PropertyChangedEventHandler? PropertyChanged;
    public string Category { get; } = category;
    internal string Key { get; } = key;
    public string Title => _definition.Title;
    public string? Description => _definition.Description;
    public int SortOrder => _definition.SortOrder;
    public ObservableCollection<CapabilityOptionState> Options { get; } = [];
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
    public bool AllowsBulkSelection => _definition.AllowsBulkSelection;
    public bool CanSelect => Options.Any(option => option.CanSelect);
    public bool? IsEnabled
    {
        get
        {
            var selectable = Options.Where(option => option.CanSelect).ToArray();
            var enabled = selectable.Count(option => option.IsEnabled);
            return enabled == 0 ? false : enabled == selectable.Length ? true : null;
        }
        set { if (value.HasValue) setEnabled(this, value.Value); }
    }

    internal void Apply(CapabilityGroupDefinition definition, IReadOnlyList<CapabilityOptionState> options)
    {
        foreach (var option in Options.Except(options)) option.PropertyChanged -= OnOptionChanged;
        foreach (var option in options.Except(Options)) option.PropertyChanged += OnOptionChanged;
        // A conflicting declaration must not broaden another package's selection UI.
        definition = definition with { AllowsBulkSelection = options.All(option => option.Definition.Group.AllowsBulkSelection) };
        var metadataChanged = _definition != definition;
        var optionsChanged = !Options.SequenceEqual(options);
        _definition = definition;
        ObservableCollectionReconciler.Apply(Options, options);
        if (metadataChanged || optionsChanged) PropertyChanged?.Invoke(this, new(null));
    }

    private void OnOptionChanged(object? sender, PropertyChangedEventArgs args)
    {
        PropertyChanged?.Invoke(this, new(nameof(IsEnabled)));
        PropertyChanged?.Invoke(this, new(nameof(CanSelect)));
    }

    public void Dispose()
    {
        foreach (var option in Options) option.PropertyChanged -= OnOptionChanged;
    }
}

internal sealed class CapabilityOptionState : INotifyPropertyChanged
{
    private bool _isEnabled;

    public CapabilityOptionState(CapabilityOptionDefinition definition, bool isEnabled)
    {
        Definition = definition;
        _isEnabled = definition.CanSelect && isEnabled;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? SelectionChanged;

    internal CapabilityOptionDefinition Definition { get; private set; }

    internal void Apply(CapabilityOptionDefinition definition, bool isEnabled)
    {
        var enabled = definition.CanSelect && isEnabled;
        var changed = Definition with { Aliases = definition.Aliases } != definition
            || !(Definition.Aliases ?? []).SequenceEqual(definition.Aliases ?? [])
            || _isEnabled != enabled;
        Definition = definition;
        _isEnabled = enabled;
        if (changed) PropertyChanged?.Invoke(this, new(null));
    }

    public string Category => Definition.Category;

    public string Kind => Definition.Kind;

    public string CapabilityId => Definition.CapabilityId;

    public string? SourceId => Definition.SourceId;

    public string DisplayName => Definition.DisplayName;

    public string? Description => Definition.Description;

    public string StatusText => Definition.StatusText;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool CanSelect => Definition.CanSelect;

    public string GroupKey => Definition.Group.Key;

    public string GroupTitle => Definition.Group.Title;

    public string? GroupDescription => Definition.Group.Description;

    public int GroupSortOrder => Definition.Group.SortOrder;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            var normalized = CanSelect && value;
            if (_isEnabled == normalized)
            {
                return;
            }

            _isEnabled = normalized;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEnabled)));
            SelectionChanged?.Invoke();
        }
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal sealed class CapabilitySelectionState : INotifyPropertyChanged
{
    private static readonly CapabilityAssignmentComparer AssignmentComparer = new();
    private static AgentProfileSelectableCapabilityAssignmentRecord CreateAssignment(string kind, string capabilityId, string? sourceId)
#if SUNDER_SUBAGENTS_APP || SUNDER_SUBAGENTS_WORKER
        => new() { Kind = kind, CapabilityId = capabilityId, SourceId = sourceId };
#else
        => new(kind, capabilityId, sourceId);
#endif
    private IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> _unresolvedAssignments = [];
    private bool _changingGroup;

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Changed;

    public ObservableCollection<CapabilityOptionState> Options { get; } = [];

    public ObservableCollection<CapabilityGroupState> Groups { get; } = [];

    public IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> Assignments
        => _unresolvedAssignments
            .Concat(Options
                .Where(option => option.IsEnabled && option.CanSelect)
                .Select(option => CreateAssignment(
                    option.Kind,
                    option.CapabilityId,
                    option.SourceId)))
            .Distinct(AssignmentComparer)
            .ToArray();

    public IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> UnresolvedAssignments
        => _unresolvedAssignments;

    public void Load(
        IEnumerable<CapabilityOptionDefinition> definitions,
        IEnumerable<AgentProfileSelectableCapabilityAssignmentRecord> assignments)
        => Apply(definitions, assignments);

    public void Reconcile(IEnumerable<CapabilityOptionDefinition> definitions)
        => Apply(definitions, Assignments);

    public void Clear()
    {
        UnsubscribeOptions();
        foreach (var group in Groups) group.Dispose();
        Options.Clear();
        Groups.Clear();
        _unresolvedAssignments = [];
        NotifyStateChanged();
    }

    public IReadOnlyList<CapabilityOptionState> GetOptions(string category)
        => Options.Where(option => string.Equals(
                option.Category,
                category,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public IReadOnlyList<CapabilityGroupState> GetGroups(string category)
        => Groups.Where(group => string.Equals(
                group.Category,
                category,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

    private void Apply(
        IEnumerable<CapabilityOptionDefinition> definitions,
        IEnumerable<AgentProfileSelectableCapabilityAssignmentRecord> assignments)
    {
        var desired = definitions
            .Where(definition => !string.IsNullOrWhiteSpace(definition.Kind)
                && !string.IsNullOrWhiteSpace(definition.CapabilityId)
                && !string.IsNullOrWhiteSpace(definition.DisplayName))
            .GroupBy(definition => string.Concat(
                    definition.Kind,
                    "\n",
                    definition.SourceId ?? string.Empty,
                    "\n",
                    definition.CapabilityId),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(definition => definition.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var assigned = assignments.Distinct(AssignmentComparer).ToArray();
        var matched = new HashSet<AgentProfileSelectableCapabilityAssignmentRecord>(AssignmentComparer);

        var retained = Options.ToDictionary(option => OptionKey(option.Definition), StringComparer.OrdinalIgnoreCase);
        var options = new List<CapabilityOptionState>();
        foreach (var definition in desired)
        {
            var matchingAssignments = assigned
                .Where(assignment => Matches(assignment, definition))
                .ToArray();
            foreach (var assignment in matchingAssignments.Where(_ => definition.CanSelect))
            {
                matched.Add(assignment);
            }

            if (retained.TryGetValue(OptionKey(definition), out var option))
                option.Apply(definition, matchingAssignments.Length > 0);
            else
            {
                option = new CapabilityOptionState(definition, matchingAssignments.Length > 0);
                option.SelectionChanged += OnOptionSelectionChanged;
            }
            options.Add(option);
        }

        foreach (var option in Options.Except(options)) option.SelectionChanged -= OnOptionSelectionChanged;
        ObservableCollectionReconciler.Apply(Options, options);

        _unresolvedAssignments = assigned
            .Where(assignment => !matched.Contains(assignment))
            .ToArray();
        RebuildGroups();
        NotifyStateChanged();
    }

    private void RebuildGroups()
    {
        var groups = Options
            .GroupBy(
                option => string.Concat(option.Category, "\n", option.GroupKey),
                StringComparer.OrdinalIgnoreCase)
            .Select(options =>
            {
                var definition = options.First().Definition;
                var group = Groups.FirstOrDefault(group => string.Equals(group.Category, definition.Category, StringComparison.OrdinalIgnoreCase)
                    && string.Equals(group.Key, definition.Group.Key, StringComparison.OrdinalIgnoreCase))
                    ?? new CapabilityGroupState(definition.Category, definition.Group.Key, SetGroupEnabled);
                group.Apply(definition.Group, options.OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray());
                return group;
            })
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase).ToArray();
        foreach (var group in Groups.Except(groups)) group.Dispose();
        ObservableCollectionReconciler.Apply(Groups, groups);
    }

    private static string OptionKey(CapabilityOptionDefinition definition)
        => string.Concat(definition.Kind, "\n", definition.SourceId ?? string.Empty, "\n", definition.CapabilityId);

    private void SetGroupEnabled(CapabilityGroupState group, bool enabled)
    {
        if (!Groups.Contains(group) || !group.AllowsBulkSelection) return;
        var changed = false;
        _changingGroup = true;
        try
        {
            foreach (var option in group.Options.Where(option => option.CanSelect && option.IsEnabled != enabled))
            {
                changed = true;
                option.IsEnabled = enabled;
            }
        }
        finally { _changingGroup = false; }
        if (changed) OnOptionSelectionChanged();
    }

    private static bool Matches(
        AgentProfileSelectableCapabilityAssignmentRecord assignment,
        CapabilityOptionDefinition definition)
    {
        var candidates = new[]
            {
                CreateAssignment(
                    definition.Kind,
                    definition.CapabilityId,
                    definition.SourceId),
            }
            .Concat(definition.Aliases ?? []);
        return candidates.Any(candidate =>
            string.Equals(assignment.Kind, candidate.Kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                assignment.CapabilityId,
                candidate.CapabilityId,
                StringComparison.OrdinalIgnoreCase)
            && SourceMatches(
                assignment.SourceId,
                candidate.SourceId,
                definition.AllowUnscopedAssignment));
    }

    private static bool SourceMatches(
        string? assignmentSourceId,
        string? capabilitySourceId,
        bool allowUnscopedAssignment)
    {
        if (allowUnscopedAssignment && string.IsNullOrWhiteSpace(assignmentSourceId))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(assignmentSourceId)
            ? string.IsNullOrWhiteSpace(capabilitySourceId)
            : !string.IsNullOrWhiteSpace(capabilitySourceId)
                && string.Equals(
                    assignmentSourceId,
                    capabilitySourceId,
                    StringComparison.OrdinalIgnoreCase);
    }

    private void OnOptionSelectionChanged()
    {
        if (_changingGroup) return;
        OnPropertyChanged(nameof(Assignments));
        Changed?.Invoke();
    }

    private void UnsubscribeOptions()
    {
        foreach (var option in Options)
        {
            option.SelectionChanged -= OnOptionSelectionChanged;
        }
    }

    private void NotifyStateChanged()
    {
        OnPropertyChanged(nameof(Assignments));
        OnPropertyChanged(nameof(UnresolvedAssignments));
        OnPropertyChanged(nameof(Options));
        OnPropertyChanged(nameof(Groups));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private sealed class CapabilityAssignmentComparer
        : IEqualityComparer<AgentProfileSelectableCapabilityAssignmentRecord>
    {
        public bool Equals(
            AgentProfileSelectableCapabilityAssignmentRecord? x,
            AgentProfileSelectableCapabilityAssignmentRecord? y)
            => ReferenceEquals(x, y)
                || x is not null
                && y is not null
                && string.Equals(x.Kind, y.Kind, StringComparison.OrdinalIgnoreCase)
                && string.Equals(x.CapabilityId, y.CapabilityId, StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    x.SourceId ?? string.Empty,
                    y.SourceId ?? string.Empty,
                    StringComparison.OrdinalIgnoreCase);

        public int GetHashCode(AgentProfileSelectableCapabilityAssignmentRecord obj)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Kind),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.CapabilityId),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.SourceId ?? string.Empty));
    }
}

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.App)]
internal static class CapabilityGrouping
{
    public static CapabilityGroupDefinition ForTool(AgentToolDescriptor descriptor)
    {
        var title = FirstNonEmpty(
            descriptor.SelectionGroupDisplayName,
            descriptor.SourceDisplayName,
            HumanizeIdentifier(descriptor.SelectionGroupId),
            HumanizeIdentifier(descriptor.SourceId),
            "Tools")!;
        var key = FirstNonEmpty(
            descriptor.SelectionGroupId,
            descriptor.SourceId,
            descriptor.SourceKind,
            title)!;
        return new CapabilityGroupDefinition(
            string.Concat("tool:", descriptor.SourceId, ":", key),
            title,
            descriptor.SelectionGroupDescription,
            10,
            descriptor.SelectionGroupAllowsBulkSelection);
    }

    public static CapabilityGroupDefinition ForPackage(
        AgentProfileSelectableCapabilityDescriptor capability)
    {
        var title = FirstNonEmpty(
            capability.GroupDisplayName,
            capability.SourceDisplayName,
            HumanizeIdentifier(capability.GroupId),
            HumanizeIdentifier(capability.SourceId),
            HumanizeIdentifier(capability.Kind),
            "Package Capabilities")!;
        var key = FirstNonEmpty(
            capability.GroupId,
            capability.SourceId,
            capability.Kind,
            title)!;
        return new CapabilityGroupDefinition(
            string.Concat("package:", capability.SourceId, ":", key),
            title,
            capability.GroupDescription,
            checked((int)capability.GroupSortOrder),
            capability.GroupAllowsBulkSelection);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? HumanizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            " ",
            value.Split(
                    ['-', '_', '.'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => string.IsNullOrEmpty(part)
                    ? part
                    : char.ToUpperInvariant(part[0]) + part[1..]));
    }
}
