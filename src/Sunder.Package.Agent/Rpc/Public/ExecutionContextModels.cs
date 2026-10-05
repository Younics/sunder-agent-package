using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcTransparent]
public readonly record struct SignedInteger
{
    public required long Value { get; init; }
}

[SunderRpcTransparent]
public readonly record struct IdentifierOrEmpty
{
    public required string Value { get; init; }
}

[SunderRpcTransparent]
public readonly record struct CommandText
{
    public required string Value { get; init; }
}

[SunderRpcTransparent]
public readonly record struct ContentText
{
    public required string Value { get; init; }
}

[SunderRpcTransparent]
public readonly record struct ProcessExitCode
{
    public required long Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct TimeoutSeconds
{
    public required long Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "1", Maximum = "2000")]
public readonly record struct PageSize
{
    public required long Value { get; init; }
}

// Retained in the frozen execution-target descriptor even though its current
// methods no longer reference this named definition.
[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct CollectionIndex
{
    public required long Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "-1")]
public readonly record struct ResourceOperationIndex
{
    public required long Value { get; init; }
}

[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
public readonly record struct Count
{
    public required long Value { get; init; }
}

public sealed class AgentResourceClaim
{
    public required IdentifierOrEmpty ActionId { get; init; }
    public required TextValue BindingGeneration { get; init; }
    public required Identifier BindingId { get; init; }
    public required Flag CaseSensitivePath { get; init; }
    public SunderRpcOptional<PathText> ConfiguredRoot { get; init; }
    public required IdentifierOrEmpty ExecutionTargetOwnerPackageId { get; init; }
    public required PathText LogicalPath { get; init; }
    public required Identifier NamespaceId { get; init; }
    public required ResourceOperationIndex ResourceIndex { get; init; }
    public required Sha256Text RootIdentityChainFingerprint { get; init; }
    public required Flag TargetExists { get; init; }
    public SunderRpcOptional<TextValue> TargetIdentity { get; init; }
    public required Flag TargetIsAuthorityRoot { get; init; }
    public SunderRpcOptional<TextValue> TargetKind { get; init; }
    public required IdentifierOrEmpty ToolCallId { get; init; }
    public required IdentifierOrEmpty ToolOwnerPackageId { get; init; }
    public required SignedInteger Version { get; init; }
    public required TextValue WorkspaceGeneration { get; init; }
    public required Identifier WorkspaceId { get; init; }
}

[SunderRpcTransparent]
public sealed class AgentResourceClaimCollection
{
    public required IReadOnlyList<AgentResourceClaim> Value { get; init; }
}

public sealed class AgentExecutionTargetContext
{
    public required Flag AllowOutsideConfiguredScope { get; init; }
    public required AgentResourceClaimCollection ApprovedResourceClaims { get; init; }
    public required TextValues ApprovedResourceReferences { get; init; }
    public required AgentWorkspaceBindingRecord Binding { get; init; }
    public SunderRpcOptional<Identifier> ProfileId { get; init; }
    public SunderRpcOptional<EntityId> SessionId { get; init; }
    public required AgentWorkspaceRecord Workspace { get; init; }
}

public sealed class AgentResourceOperationContextWire
{
    public required IdentifierOrEmpty ActionId { get; init; }
    public required Identifier AuthorityActivationId { get; init; }
    public required Count AuthorityUseCount { get; init; }
    public required TextValue BindingGeneration { get; init; }
    public required Flag CanIssueOutsideAuthority { get; init; }
    public required Identifier ExecutionTargetOwnerPackageId { get; init; }
    public required ResourceOperationIndex ResourceIndex { get; init; }
    public required EntityId RunId { get; init; }
    public required Revision RunRevision { get; init; }
    public required Identifier ToolCallId { get; init; }
    public required Identifier ToolOwnerPackageId { get; init; }
    public required TextValue WorkspaceGeneration { get; init; }
}

public sealed class AgentExecutionTargetContextWire
{
    public required TextValues ApprovedResourceCapabilities { get; init; }
    public required Flag CapturePostMutationResource { get; init; }
    public required AgentExecutionTargetContext Context { get; init; }
    public SunderRpcOptional<TextValue> ExpectedConfigurationGeneration { get; init; }
    public SunderRpcOptional<AgentResourceOperationContextWire> ResourceOperation { get; init; }
}

[SunderRpcTransparent]
public sealed class ConfigurationGenerationResolution
{
    public required TextValue? Value { get; init; }
}

public enum AgentExecutionTargetReadinessStatus
{
    [JsonStringEnumMemberName("ready")] Ready,
    [JsonStringEnumMemberName("needs-configuration")] NeedsConfiguration,
    [JsonStringEnumMemberName("failed")] Failed,
}

public sealed class AgentExecutionTargetReadiness
{
    public required DisplayText Message { get; init; }
    public required AgentExecutionTargetReadinessStatus Status { get; init; }
    public required Identifier TargetId { get; init; }
    public required TextValue TargetKind { get; init; }
}

public sealed class AgentExecutionShellDescriptor
{
    public required DisplayText Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required PathText ExecutablePath { get; init; }
    public required Identifier ShellId { get; init; }
    public required TextValue SyntaxKind { get; init; }
}

public sealed class ExecutionPathRequest
{
    public required AgentExecutionTargetContextWire Context { get; init; }
    public required PathText Path { get; init; }
}
