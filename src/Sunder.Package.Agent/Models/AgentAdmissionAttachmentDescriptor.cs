namespace Sunder.Package.Agent.Models;

internal sealed record AgentAdmissionAttachmentDescriptor(
    string FileName,
    string? MediaType,
    long SizeBytes,
    string Sha256);
