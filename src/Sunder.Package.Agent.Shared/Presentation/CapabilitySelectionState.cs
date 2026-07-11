using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed record CapabilityGroupDefinition(
    string Key,
    string Title,
    string? Description,
    int SortOrder);

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

internal sealed record CapabilityGroupState(
    string Category,
    string Title,
    string? Description,
    int SortOrder,
    IReadOnlyList<CapabilityOptionState> Options)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
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

    internal CapabilityOptionDefinition Definition { get; }

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

internal sealed class CapabilitySelectionState : INotifyPropertyChanged
{
    private static readonly CapabilityAssignmentComparer AssignmentComparer = new();
    private IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> _unresolvedAssignments = [];

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action? Changed;

    public ObservableCollection<CapabilityOptionState> Options { get; } = [];

    public ObservableCollection<CapabilityGroupState> Groups { get; } = [];

    public IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> Assignments
        => _unresolvedAssignments
            .Concat(Options
                .Where(option => option.IsEnabled && option.CanSelect)
                .Select(option => new AgentProfileSelectableCapabilityAssignmentRecord(
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

        UnsubscribeOptions();
        Options.Clear();
        foreach (var definition in desired)
        {
            var matchingAssignments = assigned
                .Where(assignment => Matches(assignment, definition))
                .ToArray();
            foreach (var assignment in matchingAssignments.Where(_ => definition.CanSelect))
            {
                matched.Add(assignment);
            }

            var option = new CapabilityOptionState(
                definition,
                definition.CanSelect && matchingAssignments.Length > 0);
            option.SelectionChanged += OnOptionSelectionChanged;
            Options.Add(option);
        }

        _unresolvedAssignments = assigned
            .Where(assignment => !matched.Contains(assignment))
            .ToArray();
        RebuildGroups();
        NotifyStateChanged();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        foreach (var group in Options
            .GroupBy(
                option => string.Concat(option.Category, "\n", option.GroupKey),
                StringComparer.OrdinalIgnoreCase)
            .Select(group => new CapabilityGroupState(
                group.First().Category,
                group.First().GroupTitle,
                group.First().GroupDescription,
                group.First().GroupSortOrder,
                group.OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray()))
            .OrderBy(group => group.SortOrder)
            .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase))
        {
            Groups.Add(group);
        }
    }

    private static bool Matches(
        AgentProfileSelectableCapabilityAssignmentRecord assignment,
        CapabilityOptionDefinition definition)
    {
        var candidates = new[]
            {
                new AgentProfileSelectableCapabilityAssignmentRecord(
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
            "tool:" + key,
            title,
            descriptor.SelectionGroupDescription,
            10);
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
            "package:" + key,
            title,
            capability.GroupDescription,
            capability.GroupSortOrder);
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
