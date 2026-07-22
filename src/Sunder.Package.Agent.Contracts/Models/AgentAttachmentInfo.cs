namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports the normalized characteristics of an attachment without persisting it.
/// </summary>
/// <remarks>
/// This value is an inspection snapshot, not proof that a subsequent store operation will succeed; cancellation,
/// storage failures, and per-message count limits can still reject the upload.
/// </remarks>
/// <param name="FileName">The normalized display file name with directory components removed.</param>
/// <param name="MediaType">The media type resolved by content inspection, extension, and the optional upload hint.</param>
/// <param name="Kind">The coarse provider-input classification.</param>
/// <param name="SizeBytes">The complete upload size in bytes.</param>
/// <param name="IsText">Whether the content can be projected as text.</param>
/// <param name="WasTruncated">Whether that text projection would exceed the base host's 524,288-character limit.</param>
public sealed record AgentAttachmentInfo(
    string FileName,
    string MediaType,
    AgentAttachmentKind Kind,
    long SizeBytes,
    bool IsText,
    bool WasTruncated);
