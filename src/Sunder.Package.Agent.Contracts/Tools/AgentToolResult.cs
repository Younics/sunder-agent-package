namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Carries a bounded tool outcome into the transcript, provider continuation, diagnostics, and UI.
/// </summary>
/// <remarks>
/// Text and structured payloads are untrusted tool output. They can be persisted and sent back to
/// the model and must not contain credentials unless the user explicitly requested secret retrieval
/// through an appropriately protected tool. Implementations should enforce output limits, set
/// <paramref name="WasTruncated" /> when omitting data, and use error results for expected failures.
/// An error result normally remains an executed call so the provider can react to it.
/// </remarks>
/// <param name="ToolId">The canonical identifier of the tool that produced the result.</param>
/// <param name="Summary">A concise status suitable for logs and transcript headers.</param>
/// <param name="Content">Optional primary text returned to the model and persisted in the transcript.</param>
/// <param name="StructuredPayloadJson">Optional valid JSON used as model content when text is absent and as structured UI data.</param>
/// <param name="Sources">Optional citations supporting the result; the host serializes and persists this list.</param>
/// <param name="WasTruncated">Whether any result data was omitted because an output limit was reached.</param>
/// <param name="IsError">Whether the tool reports a recoverable invocation or backend failure.</param>
/// <param name="ErrorCode">An optional stable machine-readable failure or runtime-control code.</param>
/// <param name="BackendId">An optional non-secret identity for the concrete server or execution target used.</param>
/// <param name="PresentationPayloadJson">Optional valid JSON reserved for UI presentation and not used as provider result content.</param>
public sealed record AgentToolResult(
    string ToolId,
    string Summary,
    string? Content = null,
    string? StructuredPayloadJson = null,
    IReadOnlyList<AgentToolSourceItem>? Sources = null,
    bool WasTruncated = false,
    bool IsError = false,
    string? ErrorCode = null,
    string? BackendId = null,
    string? PresentationPayloadJson = null);
