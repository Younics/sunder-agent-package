namespace Sunder.Package.Agent.Contracts.Models;

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
    string? BackendId);
