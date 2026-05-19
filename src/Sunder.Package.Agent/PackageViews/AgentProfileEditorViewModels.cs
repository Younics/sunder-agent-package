using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

public enum AgentProfileStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

public sealed record ProviderOption(string? Id, string Label, string? PackageId = null);

public sealed record ModelOption(
    string? Id,
    string Label,
    IReadOnlyList<AgentModelVariantDescriptor>? Variants = null
);

public sealed record ModelReasoningOption(
    string? VariantId,
    string Label,
    string? Description = null
)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

public sealed record BehaviorLoopOption(
    string LoopId,
    string? SourceId,
    string Label,
    string Description
);

public sealed record ProfileCapabilityGroupViewModel(
    string Title,
    string? Description,
    int SortOrder,
    IReadOnlyList<ProfileCapabilityOptionViewModel> Options
)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed record CapabilityGroupInfo(
    string Key,
    string Title,
    string? Description,
    int SortOrder
);

public sealed partial class ProfileCapabilityOptionViewModel : ObservableObject
{
    public ProfileCapabilityOptionViewModel(
        string kind,
        string capabilityId,
        string? sourceId,
        string displayName,
        string? description,
        string statusText,
        bool isEnabled,
        bool canSelect = true,
        string? groupKey = null,
        string? groupTitle = null,
        string? groupDescription = null,
        int groupSortOrder = 0
    )
    {
        Kind = kind;
        CapabilityId = capabilityId;
        SourceId = sourceId;
        DisplayName = displayName;
        Description = description;
        StatusText = statusText;
        CanSelect = canSelect;
        GroupKey = string.IsNullOrWhiteSpace(groupKey) ? kind : groupKey.Trim();
        GroupTitle = string.IsNullOrWhiteSpace(groupTitle) ? kind : groupTitle.Trim();
        GroupDescription = string.IsNullOrWhiteSpace(groupDescription)
            ? null
            : groupDescription.Trim();
        GroupSortOrder = groupSortOrder;
        _isEnabled = canSelect && isEnabled;
    }

    public event Action? SelectionChanged;

    public string Kind { get; }

    public string CapabilityId { get; }

    public string? SourceId { get; }

    public string DisplayName { get; }

    public string? Description { get; }

    public string StatusText { get; }

    public bool CanSelect { get; }

    public string GroupKey { get; }

    public string GroupTitle { get; }

    public string? GroupDescription { get; }

    public int GroupSortOrder { get; }

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!CanSelect && value)
        {
            IsEnabled = false;
            return;
        }

        SelectionChanged?.Invoke();
    }
}
