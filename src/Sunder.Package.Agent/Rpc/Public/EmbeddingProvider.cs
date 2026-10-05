using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.embedding.provider",
    ServiceId = "embedding-provider", IncludeSchemaDialect = true)]
[SunderRpcRuntimeUse(Actions = ["discover", "invoke"])]
public interface EmbeddingProviderService
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentEmbeddingProviderDescriptor> DescribeAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentEmbeddingModelDescriptorCollection> ListModelsAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(Unit request, CancellationToken cancellationToken = default);
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent)]
    ValueTask<TextValue> SpaceIdentityAsync(StringRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod]
    ValueTask<EmbeddingGenerationResult> GenerateAsync(EmbeddingRequest request, CancellationToken cancellationToken = default);
    [SunderRpcMethod]
    ValueTask<EmbeddingBatchResults> GenerateBatchAsync(EmbeddingBatchRequest request, CancellationToken cancellationToken = default);
}

public enum AgentAuthMode
{
    [JsonStringEnumMemberName("api-key")] ApiKey,
    [JsonStringEnumMemberName("codex-connected")] CodexConnected,
}

[SunderRpcTransparent]
public sealed class AgentAuthModeCollection
{
    public required IReadOnlyList<AgentAuthMode> Value { get; init; }
}

public sealed class AgentEmbeddingProviderDescriptor
{
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<Identifier> PackageId { get; init; }
    public required Identifier ProviderId { get; init; }
    public required AgentAuthModeCollection SupportedAuthModes { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "1")]
public readonly record struct EmbeddingDimensions
{
    public required long Value { get; init; }
}

public sealed class AgentEmbeddingModelDescriptor
{
    public SunderRpcOptional<EmbeddingDimensions> Dimensions { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required Flag IsRecommended { get; init; }
    public required Identifier ModelId { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentEmbeddingModelDescriptorCollection
{
    public required IReadOnlyList<AgentEmbeddingModelDescriptor> Value { get; init; }
}

public enum AgentProviderReadinessStatus
{
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("needs-configuration")] NeedsConfiguration,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed class AgentEmbeddingProviderReadiness
{
    public required DisplayText Message { get; init; }
    public required Identifier ProviderId { get; init; }
    public required AgentProviderReadinessStatus Status { get; init; }
}

public sealed class EmbeddingRequest
{
    public required Identifier ModelId { get; init; }
    public required TextValue Text { get; init; }
}

[SunderRpcTransparent]
public readonly record struct EmbeddingValue
{
    public required double Value { get; init; }
}

[SunderRpcTransparent]
public sealed class EmbeddingVector
{
    public required IReadOnlyList<EmbeddingValue> Value { get; init; }
}

public sealed class AgentEmbeddingGenerationResult
{
    public required EmbeddingDimensions Dimensions { get; init; }
    public required Identifier ModelId { get; init; }
    public required EmbeddingVector Values { get; init; }
}

[SunderRpcTransparent]
public sealed class EmbeddingGenerationResult
{
    public required AgentEmbeddingGenerationResult? Value { get; init; }
}

public sealed class EmbeddingBatchRequest
{
    public required Identifier ModelId { get; init; }
    public required TextValues Texts { get; init; }
}

[SunderRpcTransparent]
public sealed class EmbeddingGenerationResultOrNull
{
    public required AgentEmbeddingGenerationResult? Value { get; init; }
}

[SunderRpcTransparent]
public sealed class EmbeddingBatchResults
{
    public required IReadOnlyList<EmbeddingGenerationResultOrNull> Value { get; init; }
}
