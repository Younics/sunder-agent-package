using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class AgentCatalogRequest
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> ChatProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> EmbeddingProviderId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentProfileRecord> Profile { get; init; }
}

internal sealed class AgentCatalogProjection
{
    public required IReadOnlyList<AgentBehaviorLoopDescriptor> BehaviorLoops { get; init; }
    public required IReadOnlyList<AgentModelDescriptor> ChatModels { get; init; }
    public required IReadOnlyList<AgentProviderDescriptor> ChatProviders { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentProviderReadiness> ChatReadiness { get; init; }
    public required IReadOnlyList<AgentEmbeddingModelDescriptor> EmbeddingModels { get; init; }
    public required IReadOnlyList<AgentEmbeddingProviderDescriptor> EmbeddingProviders { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentEmbeddingProviderReadiness> EmbeddingReadiness { get; init; }
    public required IReadOnlyList<AgentExecutionTargetDescriptor> ExecutionTargets { get; init; }
    public required bool HasEmbeddingConsumers { get; init; }
    public required IReadOnlyList<AgentToolCatalogEntry> LocalTools { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long Revision { get; init; }
    public required IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> SelectableCapabilities { get; init; }
}

internal sealed class AgentBehaviorLoopDescriptor
{
    public required string Description { get; init; }
    public required string DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<string>> FeatureKinds { get; init; }
    public required string LoopId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> SettingsSchemaJson { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceId { get; init; }
}

internal sealed class AgentModelDescriptor
{
    [SunderRpcFacet(Minimum = "0")] public required long ContextWindow { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsRecommended { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long MaxOutputTokens { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentModelModeOptionDescriptor>> ModeOptions { get; init; }
    public required string ModelId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<ModelReleaseDate> ReleaseDate { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentModelSpeedOptionDescriptor>> SpeedOptions { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<AgentModelVariantDescriptor>> Variants { get; init; }
}

internal sealed class AgentModelModeOptionDescriptor
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    public required bool DisablesReasoning { get; init; }
    public required string DisplayName { get; init; }
    public required string ModeOptionId { get; init; }
}

internal sealed class AgentModelSpeedOptionDescriptor
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    public required string DisplayName { get; init; }
    public required string SpeedOptionId { get; init; }
}

internal sealed class AgentModelVariantDescriptor
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    public required string DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentReasoningEffort> ReasoningEffort { get; init; }
    public required string VariantId { get; init; }
}

internal sealed class AgentProviderDescriptor
{
    public required string DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> PackageId { get; init; }
    public required string ProviderId { get; init; }
    public required IReadOnlyList<AgentAuthMode> SupportedAuthModes { get; init; }
    public required bool SupportsInterruptibleRuns { get; init; }
    public required bool SupportsStreaming { get; init; }
}

internal sealed class AgentProviderReadiness
{
    public required string Message { get; init; }
    public required string ProviderId { get; init; }
    public required AgentProviderReadinessStatus Status { get; init; }
}

internal sealed class AgentEmbeddingProviderReadiness
{
    public required string Message { get; init; }
    public required string ProviderId { get; init; }
    public required AgentProviderReadinessStatus Status { get; init; }
}

internal sealed class AgentEmbeddingModelDescriptor
{
    [JsonIgnore(Condition = WhenWritingDefault), SunderRpcFacet(Minimum = "1")] public SunderRpcOptional<long> Dimensions { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsRecommended { get; init; }
    public required string ModelId { get; init; }
}

internal sealed class AgentEmbeddingProviderDescriptor
{
    public required string DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> PackageId { get; init; }
    public required string ProviderId { get; init; }
    public required IReadOnlyList<AgentAuthMode> SupportedAuthModes { get; init; }
}

internal sealed class AgentExecutionTargetDescriptor
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    public required string DisplayName { get; init; }
    public required IReadOnlyList<string> Facets { get; init; }
    public required bool SupportsFiles { get; init; }
    public required bool SupportsShell { get; init; }
    public required string TargetId { get; init; }
    public required string TargetKind { get; init; }
}

internal sealed class AgentToolCatalogEntry
{
    public required AgentToolDescriptor Descriptor { get; init; }
    public required AgentToolReadiness Readiness { get; init; }
}

internal sealed class AgentToolReadiness
{
    public required string Message { get; init; }
    public required AgentToolReadinessStatus Status { get; init; }
    public required string ToolId { get; init; }
}

internal sealed class AgentToolDescriptor
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<AgentToolActivationRequirement> ActivationRequirement { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<IReadOnlyList<string>> Aliases { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<JsonText> ArgumentsJsonSchema { get; init; }
    public required AgentToolConcurrencyMode ConcurrencyMode { get; init; }
    public required string Description { get; init; }
    public required string DisplayName { get; init; }
    public required bool IsReadOnly { get; init; }
    public required AgentToolPriority Priority { get; init; }
    public required bool RequiresNetwork { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> RuntimeInstructions { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SelectionGroupDescription { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SelectionGroupDisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SelectionGroupId { get; init; }
    public required AgentToolSelectionScope SelectionScope { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceDisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceKind { get; init; }
    public required string ToolId { get; init; }
}

internal sealed class AgentToolActivationRequirement
{
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> CapabilityId { get; init; }
    public required string CapabilityKind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceId { get; init; }
}

internal sealed class AgentProfileSelectableCapabilityDescriptor
{
    public required string CapabilityId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> Description { get; init; }
    public required string DisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> GroupDescription { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> GroupDisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> GroupId { get; init; }
    public required long GroupSortOrder { get; init; }
    public required bool IsSelectable { get; init; }
    public required string Kind { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceDisplayName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> SourceId { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> StatusText { get; init; }
}
