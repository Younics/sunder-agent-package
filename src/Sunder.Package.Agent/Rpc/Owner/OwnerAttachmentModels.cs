using System.Text.Json.Serialization;
using Sunder.Sdk.Rpc;
using static System.Text.Json.Serialization.JsonIgnoreCondition;

namespace Sunder.Package.Agent.Rpc.Owner;

internal sealed class AgentAttachmentMetadata
{
    public required Uuid AttachmentId { get; init; }
    public required string FileName { get; init; }
    public required bool IsText { get; init; }
    public required AgentAttachmentKind Kind { get; init; }
    public required string MediaType { get; init; }
    [SunderRpcFacet(MinLength = 64, MaxLength = 64)] public required string Sha256 { get; init; }
    [SunderRpcFacet(Minimum = "0")] public required long SizeBytes { get; init; }
    public required string StorageRelativePath { get; init; }
    public required bool WasTruncated { get; init; }
}

internal sealed class AgentAttachmentUploadDescriptor
{
    public required string FileName { get; init; }
    [JsonIgnore(Condition = WhenWritingDefault)] public SunderRpcOptional<string> MediaType { get; init; }
    [SunderRpcFacet(MinLength = 64, MaxLength = 64)] public required string Sha256 { get; init; }
    [SunderRpcFacet(Minimum = "1")] public required long SizeBytes { get; init; }
}

internal sealed class AgentAttachmentUploadHandle { public required string TransferId { get; init; } }

internal sealed class AgentAttachmentContentUploadRequest
{
    public required AgentAttachmentUploadDescriptor Upload { get; init; }
    public required ContentReference Content { get; init; }
}

internal sealed class AgentAttachmentContentUploadResult { public required AgentAttachmentUploadHandle Handle { get; init; } }
internal sealed class AgentAttachmentContentDownloadRequest { public required AgentAttachmentMetadata Metadata { get; init; } }
internal sealed class AgentAttachmentContentDownloadResult { public required ContentReference Content { get; init; } }
internal sealed class AgentAttachmentContentDiscardRequest { public required AgentAttachmentUploadHandle Handle { get; init; } }
internal sealed class AgentAttachmentContentDiscardResult { public required bool Discarded { get; init; } }
