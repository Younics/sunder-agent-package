namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentToolResult(
    string ToolId,
    string Summary,
    string? Content = null,
    string? StructuredPayloadJson = null,
    IReadOnlyList<AgentToolSourceItem>? Sources = null,
    bool WasTruncated = false,
    bool IsError = false,
    string? ErrorCode = null,
    string? BackendId = null);
