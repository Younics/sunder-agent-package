namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentAttachmentInfo(
    string FileName,
    string MediaType,
    AgentAttachmentKind Kind,
    long SizeBytes,
    bool IsText,
    bool WasTruncated);
