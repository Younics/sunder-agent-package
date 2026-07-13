namespace Sunder.Package.Agent.Contracts.Models;

public enum AgentEditorFieldKind
{
    Text = 0,
    Select = 1,
    PathList = 2,
}

public enum AgentEditorActionKind
{
    OpenPackageSettings = 0,
    RefreshEditor = 1,
    RefreshField = 2,
}

public sealed record AgentEditorSection(
    string SectionId,
    string Title,
    string? Description,
    IReadOnlyList<AgentEditorField> Fields);

public sealed record AgentEditorField(
    string FieldId,
    string Label,
    AgentEditorFieldKind Kind,
    string? Description = null,
    string? Value = null,
    IReadOnlyList<AgentEditorOption>? Options = null,
    IReadOnlyList<AgentEditorListItem>? Items = null,
    string? AddItemLabel = null,
    bool UseFolderPicker = false,
    string? DefaultNewItemValue = null)
{
    public string? ItemValueLabel { get; init; }

    public string? SecondaryItemValueLabel { get; init; }

    public bool UseSecondaryFolderPicker { get; init; }

    public string? DefaultNewSecondaryItemValue { get; init; }

    public IReadOnlyList<AgentEditorAction>? Actions { get; init; }
}

public sealed record AgentEditorOption(string Value, string Label, string? Description = null);

public sealed record AgentEditorListItem(string ItemId, string Value, bool IsDefault = false)
{
    public string? SecondaryValue { get; init; }
}

public sealed record AgentEditorAction(
    string ActionId,
    string Label,
    AgentEditorActionKind Kind,
    string? PackageId = null,
    IReadOnlyDictionary<string, string?>? Parameters = null);

public sealed record AgentEditorFieldValue(
    string? Value = null,
    IReadOnlyList<AgentEditorListItem>? Items = null);

public sealed record AgentEditorSaveRequest(
    string SectionId,
    IReadOnlyDictionary<string, AgentEditorFieldValue> Fields);

public sealed record AgentEditorSaveResult(
    bool Success,
    string Message)
{
    public static AgentEditorSaveResult Ok(string message) => new(true, message);

    public static AgentEditorSaveResult Failed(string message) => new(false, message);
}
