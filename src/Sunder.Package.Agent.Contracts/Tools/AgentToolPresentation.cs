namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Supplies persisted tool call and result data to a transcript presentation resolver.
/// </summary>
/// <remarks>Every payload is untrusted and may be malformed, truncated, or contain sensitive workspace data.</remarks>
/// <param name="ToolId">The persisted tool identifier.</param>
/// <param name="ArgumentsJson">The raw model-supplied argument JSON, which may be malformed.</param>
/// <param name="ResultSummary">The persisted concise result summary.</param>
/// <param name="TextContent">The persisted textual result returned to the model.</param>
/// <param name="StructuredPayloadJson">Optional persisted structured result JSON.</param>
/// <param name="SourcesJson">Optional persisted JSON containing <see cref="AgentToolSourceItem" /> entries.</param>
/// <param name="IsError">Whether the tool reported an error result.</param>
/// <param name="ErrorCode">The optional machine-readable tool error code.</param>
/// <param name="BackendId">The optional backend identity that executed the call.</param>
public sealed record AgentToolPresentationRequest(
    string ToolId,
    string ArgumentsJson,
    string? ResultSummary,
    string? TextContent,
    string? StructuredPayloadJson,
    string? SourcesJson,
    bool IsError,
    string? ErrorCode,
    string? BackendId);

/// <summary>
/// Provides optional display fields for a tool transcript row.
/// </summary>
/// <remarks>
/// Presentation does not change the persisted result or content sent to the model. The host may
/// fill omitted fields from its fallback presentation. Markdown and output must be bounded, safe to
/// render as untrusted content, and free of credentials.
/// </remarks>
/// <param name="HeaderText">An optional compact, single-row summary.</param>
/// <param name="DetailMarkdown">Optional untrusted Markdown describing arguments or structured details.</param>
/// <param name="OutputText">Optional plain-text output shown for the result.</param>
public sealed record AgentToolPresentation(
    string? HeaderText = null,
    string? DetailMarkdown = null,
    string? OutputText = null);
