using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Memory.Semantic.Services;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed class MemoryListItemViewModel(StoredMemoryRecord record, MemorySemanticIndexStatusRecord semanticIndexStatus) : ObservableObject
{
    internal StoredMemoryRecord Record { get; } = record;
    internal MemorySemanticIndexStatusRecord SemanticStatus { get; private set; } = semanticIndexStatus;

    internal void ApplySemanticStatus(MemorySemanticIndexStatusRecord status)
    {
        if (SemanticStatus == status) return;
        SemanticStatus = status;
        OnPropertyChanged(nameof(SemanticIndexBadgeText));
        OnPropertyChanged(nameof(SemanticIndexStatusText));
        OnPropertyChanged(nameof(HasSemanticIndexStatus));
    }

    public Guid MemoryId { get; } = record.MemoryId;
    public string Category { get; } = record.Category;
    public string CategoryLabel { get; } = FormatCategory(record.Category);
    public string Content { get; } = record.Content;
    public string PreviewText { get; } = record.Content.Length <= 140 ? record.Content : record.Content[..137].TrimEnd() + "...";
    public bool IsPinned { get; } = record.IsPinned;
    public string State { get; } = record.State;
    public string StateLabel { get; } = record.State;
    public Guid? SupersededByMemoryId { get; } = record.SupersededByMemoryId;
    public string SemanticIndexBadgeText => SemanticStatus.StatusLabel;
    public string SemanticIndexStatusText => SemanticStatus.StatusText;
    public bool HasSemanticIndexStatus => !string.IsNullOrWhiteSpace(SemanticIndexBadgeText);
    public string UpdatedAtLabel { get; } = record.UpdatedAtUtc.ToLocalTime().ToString("g");

    private static string FormatCategory(string category)
        => string.Join(' ', category.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

public sealed class MemoryEvidenceItemViewModel(StoredMemoryEvidenceRecord record)
{
    internal StoredMemoryEvidenceRecord Record { get; } = record;
    public string Text { get; } = string.IsNullOrWhiteSpace(record.EvidenceText) ? "(No evidence text)" : record.EvidenceText;
    public string TimestampLabel { get; } = record.CreatedAtUtc.ToLocalTime().ToString("g");
}
