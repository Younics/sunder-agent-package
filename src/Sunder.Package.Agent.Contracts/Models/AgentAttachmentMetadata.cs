namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentAttachmentMetadata(
    Guid AttachmentId,
    string FileName,
    string MediaType,
    AgentAttachmentKind Kind,
    long SizeBytes,
    string Sha256,
    string StorageRelativePath,
    bool IsText,
    bool WasTruncated);
