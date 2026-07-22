namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Requests a correlated child session and run on behalf of one parent tool call.
/// </summary>
/// <remarks>
/// The base executor persists an internal copy of <paramref name="ChildProfile"/> and reuses an existing child
/// session when a compatible non-empty <paramref name="TaskId"/> is found. Parent identifiers and revision form a
/// provenance correlation, but this record does not prove that the parent run is still current. Callers should create
/// it only from current, fenced tool execution. Base run preparation rejects a workspace different from the parent
/// session's assignment rather than silently reassigning the child.
/// </remarks>
/// <param name="ParentSessionId">The session that owns the delegating tool call.</param>
/// <param name="ParentRunId">The exact durable parent run issuing the request.</param>
/// <param name="ParentRunRevision">The parent's per-session run generation, paired with <paramref name="ParentRunId"/> for durable correlation and later continuation fencing.</param>
/// <param name="ParentToolCallId">The tool-call correlation identifier that owns the child result.</param>
/// <param name="WorkspaceId">The parent session's assigned workspace identifier; the base host rejects a different workspace.</param>
/// <param name="TaskId">An optional stable task key used for idempotent child-session reuse.</param>
/// <param name="ChildProfile">The profile snapshot to persist as a runtime-managed child profile.</param>
/// <param name="UserMessage">The focused task message to persist and execute in the child session.</param>
/// <param name="Title">The desired child-session title; a blank value falls back to the child profile name.</param>
/// <param name="AgentKind">An extension-defined child-session discriminator.</param>
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

/// <summary>
/// Reports the durable terminal or suspended state observed after executing a child-run request.
/// </summary>
/// <param name="SessionId">The stable child-session identifier, including when a prior task session was reused.</param>
/// <param name="Status">The child run's persisted status.</param>
/// <param name="Summary">The checkpoint summary, or a status-derived fallback when no summary was persisted.</param>
/// <param name="Content">The latest persisted assistant message text from the child session, or <see langword="null"/> when none exists.</param>
/// <param name="Title">The persisted child-session title, when available.</param>
public sealed record AgentChildRunResult(
    Guid SessionId,
    AgentRunStatus Status,
    string Summary,
    string? Content = null,
    string? Title = null);
