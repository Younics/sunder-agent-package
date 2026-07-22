namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents an immutable persisted snapshot of one Agent conversation session.
/// </summary>
/// <remarks>
/// Session state reflects the newest applicable run checkpoint; a later run revision can make a previously terminal
/// session active again. Parent correlation fields describe child-run provenance and must be considered together.
/// </remarks>
/// <param name="SessionId">The stable identifier of the session.</param>
/// <param name="Title">The persisted user-facing session title.</param>
/// <param name="State">The session lifecycle projection at the time of this snapshot.</param>
/// <param name="CreatedAtUtc">The UTC time at which the session was created.</param>
/// <param name="UpdatedAtUtc">The UTC time of the latest persisted session, transcript, checkpoint, or context change.</param>
/// <param name="ParentSessionId">The immediate parent session for a child run, or <see langword="null"/> for a root session.</param>
/// <param name="RootSessionId">The root of the session tree; root sessions normally identify themselves.</param>
/// <param name="ParentRunId">The exact parent run that created this child session, when applicable.</param>
/// <param name="ParentRunRevision">The per-session parent run generation paired with <paramref name="ParentRunId"/>.</param>
/// <param name="ParentToolCallId">The tool-call correlation identifier that launched the child session.</param>
/// <param name="TaskId">An optional caller-defined task identity used to find and reuse a child session.</param>
/// <param name="ProfileId">The profile selected for this session, or <see langword="null"/> for legacy or incomplete records.</param>
/// <param name="BehaviorLoopId">The behavior-loop selection captured for the session, if one was assigned.</param>
/// <param name="AgentKind">An optional extension-defined discriminator such as <c>agent</c> or <c>subagent</c>.</param>
/// <param name="WorkspaceId">The assigned workspace identifier, or <see langword="null"/> only for legacy or incomplete records.</param>
public sealed record AgentSessionRecord(
    Guid SessionId,
    string Title,
    AgentSessionState State,
    DateTimeOffset CreatedAtUtc,
    DateTimeOffset UpdatedAtUtc,
    Guid? ParentSessionId = null,
    Guid? RootSessionId = null,
    Guid? ParentRunId = null,
    long? ParentRunRevision = null,
    string? ParentToolCallId = null,
    string? TaskId = null,
    string? ProfileId = null,
    string? BehaviorLoopId = null,
    string? AgentKind = null,
    string? WorkspaceId = null);
