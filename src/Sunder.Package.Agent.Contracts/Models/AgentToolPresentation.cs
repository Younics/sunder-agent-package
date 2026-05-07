namespace Sunder.Package.Agent.Contracts.Models;

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

public sealed record AgentToolPresentation(
    string? HeaderText = null,
    string? DetailMarkdown = null,
    string? OutputText = null);
