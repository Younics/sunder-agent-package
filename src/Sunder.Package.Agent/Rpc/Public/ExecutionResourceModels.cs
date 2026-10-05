using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

public enum AgentPermissionScopeClassificationBasis
{
    [JsonStringEnumMemberName("unspecified")] Unspecified,
    [JsonStringEnumMemberName("opened-ancestor-identity")] OpenedAncestorIdentity,
    [JsonStringEnumMemberName("lexical-and-opened-identity")] LexicalAndOpenedIdentity,
    [JsonStringEnumMemberName("verified-target-mapping")] VerifiedTargetMapping,
    [JsonStringEnumMemberName("unresolved")] Unresolved,
    [JsonStringEnumMemberName("mixed")] Mixed,
    [JsonStringEnumMemberName("lexical-exclusion")] LexicalExclusion,
}

public sealed class AgentResolvedResource
{
    public required TextValue CanonicalReference { get; init; }
    public SunderRpcOptional<TextValue> DeleteCanonicalReference { get; init; }
    public SunderRpcOptional<Identifier> DeletePermissionBoundaryId { get; init; }
    public SunderRpcOptional<AgentResourceClaim> DeleteResourceClaim { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required Flag Exists { get; init; }
    public required Flag IsScopedInstructionDocument { get; init; }
    public required Identifier PermissionBoundaryId { get; init; }
    public SunderRpcOptional<AgentResourceClaim> ResourceClaim { get; init; }
    public required TextValue ResourceKind { get; init; }
    public required AgentPermissionScopeClassificationBasis ScopeClassificationBasis { get; init; }
}

public sealed class AgentResolvedResourceWire
{
    public required TextValues AuthorityReferences { get; init; }
    public required TextValues DeleteAuthorityReferences { get; init; }
    public required AgentResolvedResource Resource { get; init; }
}

public enum AgentExecutionResourceAccessMode
{
    [JsonStringEnumMemberName("read-only")] ReadOnly,
    [JsonStringEnumMemberName("read-write")] ReadWrite,
}

[SunderRpcTransparent, SunderRpcFacet(MinLength = 1)]
public sealed class TextMetadata
{
    public required IReadOnlyDictionary<string, TextValue> Value { get; init; }
}

public sealed class AgentExecutionResourceDescriptor
{
    public required AgentExecutionResourceAccessMode AccessMode { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required PathText HostPath { get; init; }
    public SunderRpcOptional<TextMetadata> Metadata { get; init; }
    public required PathText PreferredExecutionPath { get; init; }
    public required Identifier ResourceId { get; init; }
    public required TextValue ResourceKind { get; init; }
    public required Identifier SourceId { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentExecutionResourceDescriptorCollection
{
    public required IReadOnlyList<AgentExecutionResourceDescriptor> Value { get; init; }
}

public sealed class ExecutionResourcesRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentExecutionResourceDescriptorCollection Resources { get; init; }
}

public sealed class AgentResolvedExecutionResource
{
    public required AgentExecutionResourceAccessMode AccessMode { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required PathText ExecutionPath { get; init; }
    public required PathText HostPath { get; init; }
    public SunderRpcOptional<TextMetadata> Metadata { get; init; }
    public required Identifier ResourceId { get; init; }
    public required TextValue ResourceKind { get; init; }
    public required Identifier SourceId { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentResolvedExecutionResourceCollection
{
    public required IReadOnlyList<AgentResolvedExecutionResource> Value { get; init; }
}

public sealed class AgentResourceAuthorityValidation
{
    public SunderRpcOptional<TextValue> ErrorCode { get; init; }
    public SunderRpcOptional<DisplayText> ErrorMessage { get; init; }
    public required Flag IsValid { get; init; }
}

public sealed class ResourceCapabilitiesRequest
{
    public required TextValues Capabilities { get; init; }
}

public sealed class AgentScopedInstructionProbe
{
    public required Flag FollowFinalSymbolicLink { get; init; }
    public required Flag IsDirectory { get; init; }
    public required PathText Path { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentScopedInstructionProbeCollection
{
    public required IReadOnlyList<AgentScopedInstructionProbe> Value { get; init; }
}

public sealed class AgentScopedInstructionDiscoveryRequest
{
    public required AgentScopedInstructionProbeCollection Probes { get; init; }
}

public sealed class ExecutionInstructionDiscoveryRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required AgentScopedInstructionDiscoveryRequest Request { get; init; }
}

public sealed class AgentScopedInstructionDocument
{
    public required PathText AppliesToDirectory { get; init; }
    public required ContentText Content { get; init; }
    public required Sha256Text ContentHash { get; init; }
    public required PathText Path { get; init; }
    public required PathText ScopeRoot { get; init; }
    public required Flag WasTruncated { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentScopedInstructionDocumentCollection
{
    public required IReadOnlyList<AgentScopedInstructionDocument> Value { get; init; }
}

public sealed class AgentScopedInstructionScope
{
    public required AgentScopedInstructionDocumentCollection Documents { get; init; }
    public required Count OmittedAncestorCount { get; init; }
    public required PathText RequestedPath { get; init; }
    public required PathText ScopeRoot { get; init; }
    public required PathText TargetDirectory { get; init; }
    public required Flag WasTruncated { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentScopedInstructionScopeCollection
{
    public required IReadOnlyList<AgentScopedInstructionScope> Value { get; init; }
}

public sealed class AgentScopedInstructionDiscoveryResult
{
    public required Count ProcessedProbeCount { get; init; }
    public required Sha256Text ScopeFingerprint { get; init; }
    public required AgentScopedInstructionScopeCollection Scopes { get; init; }
    public required Sha256Text TargetFingerprint { get; init; }
    public required Flag WasTruncated { get; init; }
}
