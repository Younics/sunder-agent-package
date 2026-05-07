namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentStoredAttachment(
    AgentAttachmentMetadata Metadata,
    string? TextContent);
