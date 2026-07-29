namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Captures one persisted, ordered content item within a transcript turn.
/// </summary>
/// <remarks>
/// Field applicability depends on <paramref name="Kind"/>. JSON payloads are opaque to this record and must be
/// validated by their producing/consuming extension. Error and backend fields describe provenance and execution
/// outcome; neither makes the associated content trusted.
/// </remarks>
/// <param name="ItemId">The stable identifier of this persisted item.</param>
/// <param name="TurnId">The identifier of the owning turn.</param>
/// <param name="SequenceNumber">The item's ascending ordinal within the turn.</param>
/// <param name="Kind">The semantic shape that determines which optional fields apply.</param>
/// <param name="TextContent">Message text, attachment text projection, or tool-result text, when applicable.</param>
/// <param name="CallId">The provider-issued correlation identifier shared by a tool call and its result, when applicable.</param>
/// <param name="ToolId">The opaque tool identifier associated with a call or result.</param>
/// <param name="ArgumentsJson">The tool-call arguments as JSON, or <see langword="null"/> for non-call items.</param>
/// <param name="ResultSummary">An optional bounded human-readable summary of a tool result.</param>
/// <param name="StructuredPayloadJson">Optional tool-defined machine-readable result JSON.</param>
/// <param name="SourcesJson">Optional producer-defined JSON containing citations or source metadata.</param>
/// <param name="WasTruncated">Whether any content was shortened or omitted while persisting or projecting this item.</param>
/// <param name="IsError">Whether the item represents a failed tool operation rather than a successful result.</param>
/// <param name="ErrorCode">An optional stable, producer-defined code for programmatic error handling.</param>
/// <param name="BackendId">An optional identifier for the backend that produced the result, useful for diagnostics and provenance.</param>
/// <param name="PresentationPayloadJson">Optional UI-specific JSON that supplements, but does not replace, the provider-facing result fields.</param>
public sealed record AgentTurnItemRecord(
    Guid ItemId,
    Guid TurnId,
    int SequenceNumber,
    AgentTurnItemKind Kind,
    string? TextContent,
    string? CallId,
    string? ToolId,
    string? ArgumentsJson,
    string? ResultSummary,
    string? StructuredPayloadJson,
    string? SourcesJson,
    bool WasTruncated,
    bool IsError,
    string? ErrorCode,
    string? BackendId,
    string? PresentationPayloadJson = null)
{
    /// <summary>Gets the host-issued durable tool execution identifier, when this item represents a tool call or result.</summary>
    public Guid? ToolExecutionId { get; init; }

    /// <summary>Gets the durable execution state projected when this transcript snapshot was loaded.</summary>
    public AgentToolExecutionStatus? ToolExecutionStatus { get; init; }

    /// <summary>Gets the package that owned the exact tool contribution when execution was prepared.</summary>
    public string? ToolOwnerPackageId { get; init; }

    /// <summary>Gets the optional stable schema identity captured when execution was prepared.</summary>
    public string? ToolSchemaId { get; init; }

    /// <summary>Gets the optional schema version captured when execution was prepared.</summary>
    public string? ToolSchemaVersion { get; init; }

    /// <summary>Gets a bounded, transport-projected hint that is safe to show without parsing tool detail payloads.</summary>
    public string? ToolHeaderHint { get; init; }

    /// <summary>Gets a bounded error summary that remains available while tool details are collapsed.</summary>
    public string? ToolErrorSummary { get; init; }

    /// <summary>Gets the authoritative detail revision represented by this lightweight transcript item.</summary>
    public long ToolDetailRevision { get; init; }

    /// <summary>Gets whether an exact detail lookup can return presentable fields for this tool invocation.</summary>
    public bool? ToolHasDetails { get; init; }

    /// <summary>Gets whether payload-heavy tool fields were intentionally omitted from this transcript projection.</summary>
    public bool IsToolHeaderProjection { get; init; }
}
