namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentChildRunRequest(
    Guid ParentSessionId,
    Guid ParentRunId,
    long ParentRunRevision,
    string ParentToolCallId,
    string WorkspaceId,
    string? TaskId,
    AgentProfileRecord ChildProfile,
    string UserMessage,
    string Title,
    string AgentKind = "subagent");

public sealed record AgentChildRunResult(
    Guid SessionId,
    AgentRunStatus Status,
    string Summary,
    string? Content = null,
    string? Title = null);
