using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Rpc.Public;

public sealed class AgentWorkspaceDocumentRecord
{
    public required Timestamp CreatedAtUtc { get; init; }
    public required Identifier DocumentId { get; init; }
    public required PathText FilePath { get; init; }
    public required SortOrder SortOrder { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required Identifier WorkspaceId { get; init; }

    public static AgentWorkspaceDocumentRecord FromDomain(Domain.AgentWorkspaceDocumentRecord value)
        => new()
        {
            CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc), DocumentId = value.DocumentId,
            FilePath = value.FilePath, SortOrder = value.SortOrder,
            UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc), WorkspaceId = value.WorkspaceId,
        };
}

[SunderRpcTransparent]
public sealed class AgentWorkspaceDocumentRecordCollection
{
    public required IReadOnlyList<AgentWorkspaceDocumentRecord> Value { get; init; }
}

public sealed class AgentWorkspacePathRecord
{
    public required Timestamp CreatedAtUtc { get; init; }
    public required PathText HostPath { get; init; }
    public required Flag IsDefault { get; init; }
    public required Identifier PathId { get; init; }
    public required SortOrder SortOrder { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required Identifier WorkspaceId { get; init; }

    public static AgentWorkspacePathRecord FromDomain(Domain.AgentWorkspacePathRecord value)
        => new()
        {
            CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc), HostPath = value.HostPath,
            IsDefault = value.IsDefault, PathId = value.PathId, SortOrder = value.SortOrder,
            UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc), WorkspaceId = value.WorkspaceId,
        };
}

[SunderRpcTransparent]
public sealed class AgentWorkspacePathRecordCollection
{
    public required IReadOnlyList<AgentWorkspacePathRecord> Value { get; init; }
}

public sealed class AgentWorkspaceRecord
{
    public required Timestamp CreatedAtUtc { get; init; }
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required AgentWorkspaceDocumentRecordCollection Documents { get; init; }
    public required AgentWorkspacePathRecordCollection Paths { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required Identifier WorkspaceId { get; init; }

    public static AgentWorkspaceRecord FromDomain(Domain.AgentWorkspaceRecord value)
        => new()
        {
            CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc), Description = DisplayText.Optional(value.Description),
            DisplayName = value.DisplayName,
            Documents = new() { Value = value.Documents.Select(AgentWorkspaceDocumentRecord.FromDomain).ToArray() },
            Paths = new() { Value = value.Paths.Select(AgentWorkspacePathRecord.FromDomain).ToArray() },
            UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc), WorkspaceId = value.WorkspaceId,
        };
}

[SunderRpcTransparent]
public sealed class AgentWorkspaceRecordCollection
{
    public required IReadOnlyList<AgentWorkspaceRecord> Value { get; init; }
}

public sealed class StringRequest
{
    public required TextValue Value { get; init; }
}

public sealed class AgentWorkspaceBindingRecord
{
    public required Identifier BindingId { get; init; }
    public required Identifier ContributionId { get; init; }
    public required Timestamp CreatedAtUtc { get; init; }
    public required Identifier ExtensionPointId { get; init; }
    public required Flag IsEnabled { get; init; }
    public required Identifier Role { get; init; }
    public required SortOrder SortOrder { get; init; }
    public required Timestamp UpdatedAtUtc { get; init; }
    public required Identifier WorkspaceId { get; init; }

    public static AgentWorkspaceBindingRecord FromDomain(Domain.AgentWorkspaceBindingRecord value)
        => new()
        {
            BindingId = value.BindingId, ContributionId = value.ContributionId,
            CreatedAtUtc = Timestamp.FromDomain(value.CreatedAtUtc), ExtensionPointId = value.ExtensionPointId,
            IsEnabled = value.IsEnabled, Role = value.Role, SortOrder = value.SortOrder,
            UpdatedAtUtc = Timestamp.FromDomain(value.UpdatedAtUtc), WorkspaceId = value.WorkspaceId,
        };
}

public sealed class AgentRpcProviderHandle
{
    public required Sha256Text AbiSha256 { get; init; }
    public required Revision ActivationEpoch { get; init; }
    public required EntityId ActivationId { get; init; }
    public required Identifier ContractId { get; init; }
    public required VersionText ContractVersion { get; init; }
    public required Sha256Text DescriptorSha256 { get; init; }
    public required TextValue EndpointReference { get; init; }
    public required Identifier PackageId { get; init; }
    public required VersionText PackageVersion { get; init; }
    public required Identifier ProviderId { get; init; }
    public required Revision SessionGeneration { get; init; }

    public Protocol.AgentRpcProviderHandle ToDomain()
        => new(PackageId.Value, PackageVersion.Value, ProviderId.Value, ContractId.Value, ContractVersion.Value,
            DescriptorSha256.Value, AbiSha256.Value, ActivationId.Value, ActivationEpoch.Value, SessionGeneration.Value, EndpointReference.Value);

    public static AgentRpcProviderHandle FromDomain(Protocol.AgentRpcProviderHandle value)
        => new()
        {
            AbiSha256 = value.AbiSha256, ActivationEpoch = value.ActivationEpoch, ActivationId = value.ActivationId,
            ContractId = value.ContractId, ContractVersion = value.ContractVersion, DescriptorSha256 = value.DescriptorSha256,
            EndpointReference = value.EndpointReference, PackageId = value.PackageId, PackageVersion = value.PackageVersion,
            ProviderId = value.ProviderId, SessionGeneration = value.SessionGeneration,
        };
}

public sealed class AgentExecutionScopeDescriptor
{
    public SunderRpcOptional<PathText> DefaultWorkingDirectory { get; init; }
    public required DisplayText DisplayName { get; init; }
    public SunderRpcOptional<PathText> PathStyleDescription { get; init; }
    public required TextValues WorkspacePaths { get; init; }

    public static AgentExecutionScopeDescriptor FromDomain(Domain.AgentExecutionScopeDescriptor value)
        => new()
        {
            DefaultWorkingDirectory = PathText.Optional(value.DefaultWorkingDirectory), DisplayName = value.DisplayName,
            PathStyleDescription = PathText.Optional(value.PathStyleDescription), WorkspacePaths = TextValues.FromDomain(value.WorkspacePaths),
        };
}

public sealed class AgentExecutionTargetDescriptor
{
    public SunderRpcOptional<DisplayText> Description { get; init; }
    public required DisplayText DisplayName { get; init; }
    public required TextValues Facets { get; init; }
    public required Flag SupportsFiles { get; init; }
    public required Flag SupportsShell { get; init; }
    public required Identifier TargetId { get; init; }
    public required TextValue TargetKind { get; init; }

    public static AgentExecutionTargetDescriptor FromDomain(Domain.AgentExecutionTargetDescriptor value)
        => new()
        {
            Description = DisplayText.Optional(value.Description), DisplayName = value.DisplayName,
            Facets = TextValues.FromDomain(value.Facets), SupportsFiles = value.SupportsFiles,
            SupportsShell = value.SupportsShell, TargetId = value.TargetId, TargetKind = value.TargetKind,
        };
}

public sealed class AgentWorkspaceExecutionResolutionWire
{
    public required AgentWorkspaceBindingRecord Binding { get; init; }
    public required AgentRpcProviderHandle ExecutionTarget { get; init; }
    public required AgentExecutionScopeDescriptor Scope { get; init; }
    public required AgentExecutionTargetDescriptor Target { get; init; }
    public required AgentWorkspaceRecord Workspace { get; init; }
}
