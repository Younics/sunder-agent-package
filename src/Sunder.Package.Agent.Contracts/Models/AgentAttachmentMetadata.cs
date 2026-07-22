namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Describes an attachment after the host has validated and persisted its original bytes.
/// </summary>
/// <remarks>
/// Metadata is an immutable snapshot. The storage path is an opaque host-relative locator rather than a
/// caller-controlled file-system path; consumers must not combine or resolve it outside the host attachment store.
/// The base Agent host accepts non-empty attachments up to 25 MiB and preserves all accepted bytes even when its
/// text projection is truncated.
/// </remarks>
/// <param name="AttachmentId">The stable identifier assigned to this stored attachment.</param>
/// <param name="FileName">The normalized leaf file name used for display; directory components are removed during validation.</param>
/// <param name="MediaType">The normalized media type selected from content inspection, file extension, and the upload hint.</param>
/// <param name="Kind">The coarse input kind used for provider capability checks.</param>
/// <param name="SizeBytes">The byte length of the complete stored content.</param>
/// <param name="Sha256">The lowercase hexadecimal SHA-256 digest of the complete stored content.</param>
/// <param name="StorageRelativePath">An opaque path relative to the host's attachment root. Host implementations reject locators that escape that root.</param>
/// <param name="IsText">Whether the host decoded a text projection for prompt use.</param>
/// <param name="WasTruncated">Whether the prompt-facing text projection was shortened; this does not indicate that stored bytes were discarded.</param>
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
