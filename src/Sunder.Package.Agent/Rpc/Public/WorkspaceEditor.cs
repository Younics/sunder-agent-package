using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.workspace.editor",
    ServiceId = "workspace-editor", IncludeSchemaDialect = true)]
[SunderRpcAppUse(Actions = ["discover", "invoke"])]
public interface WorkspaceEditorService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentRpcIdentity> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<Flag> CanEditAsync(AgentWorkspaceEditorContext request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentEditorSectionCollection> GetSectionsAsync(AgentWorkspaceEditorContext request, CancellationToken cancellationToken = default);
    [SunderRpcMethod]
    ValueTask<AgentEditorSaveResult> SaveSectionAsync(WorkspaceEditorSaveRequest request, CancellationToken cancellationToken = default);
}

public sealed class AgentWorkspaceEditorContext
{
    public required Identifier ConfigurationId { get; init; }
    public required Identifier TargetId { get; init; }
    public required AgentWorkspaceRecord Workspace { get; init; }
}

public enum AgentEditorActionKind
{
    [JsonStringEnumMemberName("open-package-settings")] OpenPackageSettings,
    [JsonStringEnumMemberName("refresh-field")] RefreshField,
}

[SunderRpcTransparent]
public sealed class TextValueOrNull
{
    public required TextValue? Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
public sealed class EditorActionParameters
{
    public required IReadOnlyDictionary<string, TextValueOrNull> Value { get; init; }
}

public sealed class AgentEditorAction
{
    public required Identifier ActionId { get; init; }
    public required AgentEditorActionKind Kind { get; init; }
    public required DisplayText Label { get; init; }
    public SunderRpcOptional<Identifier> PackageId { get; init; }
    public SunderRpcOptional<EditorActionParameters> Parameters { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentEditorActionCollection
{
    public required IReadOnlyList<AgentEditorAction> Value { get; init; }
}

public sealed class AgentEditorListItem
{
    public required Flag IsDefault { get; init; }
    public required Identifier ItemId { get; init; }
    public SunderRpcOptional<TextValue> SecondaryValue { get; init; }
    public required TextValue Value { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentEditorListItemCollection
{
    public required IReadOnlyList<AgentEditorListItem> Value { get; init; }
}

public enum AgentEditorFieldKind
{
    [JsonStringEnumMemberName("text")] Text,
    [JsonStringEnumMemberName("select")] Select,
    [JsonStringEnumMemberName("path-list")] PathList,
}

public sealed class AgentEditorOption
{
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required DisplayText Label { get; init; }
    public required TextValue Value { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentEditorOptionCollection
{
    public required IReadOnlyList<AgentEditorOption> Value { get; init; }
}

public sealed class AgentEditorField
{
    public SunderRpcOptional<AgentEditorActionCollection> Actions { get; init; }
    public SunderRpcOptional<DisplayText> AddItemLabel { get; init; }
    public SunderRpcOptional<TextValue> DefaultNewItemValue { get; init; }
    public SunderRpcOptional<TextValue> DefaultNewSecondaryItemValue { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required Identifier FieldId { get; init; }
    public SunderRpcOptional<DisplayText> ItemValueLabel { get; init; }
    public SunderRpcOptional<AgentEditorListItemCollection> Items { get; init; }
    public required AgentEditorFieldKind Kind { get; init; }
    public required DisplayText Label { get; init; }
    public SunderRpcOptional<AgentEditorOptionCollection> Options { get; init; }
    public SunderRpcOptional<DisplayText> SecondaryItemValueLabel { get; init; }
    public required Flag UseFolderPicker { get; init; }
    public required Flag UseSecondaryFolderPicker { get; init; }
    public SunderRpcOptional<TextValue> Value { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentEditorFieldCollection
{
    public required IReadOnlyList<AgentEditorField> Value { get; init; }
}

public sealed class AgentEditorSection
{
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required AgentEditorFieldCollection Fields { get; init; }
    public required Identifier SectionId { get; init; }
    public required DisplayText Title { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentEditorSectionCollection
{
    public required IReadOnlyList<AgentEditorSection> Value { get; init; }
}

public sealed class AgentEditorFieldValue
{
    public SunderRpcOptional<AgentEditorListItemCollection> Items { get; init; }
    public SunderRpcOptional<TextValue> Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
public sealed class EditorFieldValues
{
    public required IReadOnlyDictionary<string, AgentEditorFieldValue> Value { get; init; }
}

public sealed class AgentEditorSaveRequest
{
    public required EditorFieldValues Fields { get; init; }
    public required Identifier SectionId { get; init; }
}

public sealed class WorkspaceEditorSaveRequest
{
    public required AgentWorkspaceEditorContext Context { get; init; }
    public required AgentEditorSaveRequest Request { get; init; }
}

public sealed class AgentEditorSaveResult
{
    public required DisplayText Message { get; init; }
    public required Flag Success { get; init; }
}
