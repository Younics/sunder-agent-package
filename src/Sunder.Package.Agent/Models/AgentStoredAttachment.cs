namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Couples persisted attachment metadata with the bounded text projection available to prompt construction.
/// </summary>
/// <remarks>
/// The original bytes are stored separately at the location represented by <see cref="AgentAttachmentMetadata"/>.
/// This immutable record does not own or copy objects reachable through its parameters.
/// </remarks>
/// <param name="Metadata">The durable metadata and host-issued storage locator for the original attachment.</param>
/// <param name="TextContent">Decoded UTF-8 text for text attachments, possibly truncated as indicated by the metadata; otherwise <see langword="null"/>.</param>
public sealed record AgentStoredAttachment(
    AgentAttachmentMetadata Metadata,
    string? TextContent);
