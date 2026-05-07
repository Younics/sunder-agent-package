namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentAttachmentUploadRequest(
    string FileName,
    string? MediaType,
    byte[] Content);
