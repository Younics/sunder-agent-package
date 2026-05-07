namespace Sunder.Package.Agent.Contracts.Models;

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
    string? AgentKind = null);
