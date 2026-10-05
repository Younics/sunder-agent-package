using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public.Chat;

[SunderRpcTransparent]
public readonly record struct ShortText { public required string Value { get; init; } }

[SunderRpcTransparent]
public readonly record struct CalendarDate { public required DateOnly Value { get; init; } }

[SunderRpcTransparent]
public readonly record struct SafeInteger { public required long Value { get; init; } }

[SunderRpcTransparent, SunderRpcFacet(Description = "A finite IEEE 754 binary32 Microsoft.Extensions.AI chat option.")]
public readonly record struct ChatOptionValue { public required float Value { get; init; } }

[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct TopK { public required long Value { get; init; } }

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
public readonly record struct MediaType { public required string Value { get; init; } }

[SunderRpcTransparent]
public readonly record struct AbsoluteUri { public required SunderRpcUri Value { get; init; } }

[SunderRpcTransparent]
public sealed class InlineContinuationBytes { public required byte[] Value { get; init; } }

[SunderRpcTransparent]
public sealed class ContentReference { public required SunderRpcContentReference Value { get; init; } }

[SunderRpcTransparent, SunderRpcDefinition("JSON")]
public readonly record struct RawJson { public required JsonElement Value { get; init; } }

[SunderRpcTransparent, SunderRpcDefinition("JSONObject"), SunderRpcFacet(JsonObjectOnly = true)]
public readonly record struct RawJsonObject { public required JsonElement Value { get; init; } }

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
public sealed class JsonValues { public required IReadOnlyDictionary<string, RawJson> Value { get; init; } }

[SunderRpcTransparent]
public sealed class SupportedAuthModes { public required IReadOnlyList<AgentAuthMode> Value { get; init; } }

public sealed class AgentProviderDescriptor
{
    public required Identifier ProviderId { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required SupportedAuthModes SupportedAuthModes { get; init; }
    public required bool SupportsStreaming { get; init; }
    public required bool SupportsInterruptibleRuns { get; init; }
    public SunderRpcOptional<Identifier> PackageId { get; init; }
}

public enum AgentReasoningEffort
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("extra-high")] ExtraHigh,
}

public sealed class AgentModelVariantDescriptor
{
    public required Identifier VariantId { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public SunderRpcOptional<AgentReasoningEffort> ReasoningEffort { get; init; }
}

[SunderRpcTransparent]
public sealed class ModelVariants { public required IReadOnlyList<AgentModelVariantDescriptor> Value { get; init; } }

public sealed class AgentModelSpeedOptionDescriptor
{
    public required Identifier SpeedOptionId { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
}

[SunderRpcTransparent]
public sealed class ModelSpeedOptions { public required IReadOnlyList<AgentModelSpeedOptionDescriptor> Value { get; init; } }

public sealed class AgentModelModeOptionDescriptor
{
    public required Identifier ModeOptionId { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required bool DisablesReasoning { get; init; }
}

[SunderRpcTransparent]
public sealed class ModelModeOptions { public required IReadOnlyList<AgentModelModeOptionDescriptor> Value { get; init; } }

public sealed class AgentModelDescriptor
{
    public required Identifier ModelId { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required TokenCount ContextWindow { get; init; }
    public required TokenCount MaxOutputTokens { get; init; }
    public required bool IsRecommended { get; init; }
    public SunderRpcOptional<ModelVariants> Variants { get; init; }
    public SunderRpcOptional<ModelSpeedOptions> SpeedOptions { get; init; }
    public SunderRpcOptional<ModelModeOptions> ModeOptions { get; init; }
    public SunderRpcOptional<CalendarDate> ReleaseDate { get; init; }
}

[SunderRpcTransparent]
public sealed class ChatModels { public required IReadOnlyList<AgentModelDescriptor> Value { get; init; } }

public sealed class AgentProviderReadiness
{
    public required Identifier ProviderId { get; init; }
    public required AgentProviderReadinessStatus Status { get; init; }
    public required DisplayText Message { get; init; }
}

public sealed class ChatCapabilitiesRequest { public SunderRpcOptional<Identifier> ModelId { get; init; } }

// This frozen chat definition uses inline booleans and its original member order;
// the prompt contract's same-named definition intentionally has a different shape.
public sealed class AgentProviderRunCapabilities
{
    public required bool SupportsNativeToolCalling { get; init; }
    public required bool SupportsStreamingToolCalls { get; init; }
    public required bool SupportsMultipleToolCalls { get; init; }
    public required DisplayText Summary { get; init; }
    public required bool SupportsImageInput { get; init; }
    public required bool SupportsPdfInput { get; init; }
    public required bool SupportsAudioInput { get; init; }
    public required bool SupportsVideoInput { get; init; }
    public SunderRpcOptional<TokenCount> ContextWindowTokens { get; init; }
    public SunderRpcOptional<TokenCount> MaxOutputTokens { get; init; }
}

[SunderRpcTransparent]
public sealed class UtilityModelResolution { public required ShortText? Value { get; init; } }

public enum ChatReasoningEffort
{
    [JsonStringEnumMemberName("none")] None,
    [JsonStringEnumMemberName("low")] Low,
    [JsonStringEnumMemberName("medium")] Medium,
    [JsonStringEnumMemberName("high")] High,
    [JsonStringEnumMemberName("extra-high")] ExtraHigh,
}

public enum AgentChatProviderFailureKind
{
    [JsonStringEnumMemberName("unknown")] Unknown,
    [JsonStringEnumMemberName("context-window-exceeded")] ContextWindowExceeded,
}
