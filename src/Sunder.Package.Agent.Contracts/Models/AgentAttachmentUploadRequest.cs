namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Carries one in-memory attachment proposed for a user message.
/// </summary>
/// <remarks>
/// Construction performs no validation and retains the supplied byte-array reference. The caller must not mutate
/// <paramref name="Content"/> while an asynchronous consumer is inspecting or storing it. The base Agent host rejects
/// empty content, content larger than 25 MiB, and message batches containing more than 10 attachments, normally with
/// <see cref="InvalidOperationException"/>; cancellation and I/O errors can also abort storage.
/// </remarks>
/// <param name="FileName">The proposed display name. The host removes directory components and replaces invalid file-name characters.</param>
/// <param name="MediaType">An optional advisory media type. Content signatures and known extensions take precedence.</param>
/// <param name="Content">The complete attachment bytes. This array is not defensively copied by the record.</param>
public sealed record AgentAttachmentUploadRequest(
    string FileName,
    string? MediaType,
    byte[] Content);
