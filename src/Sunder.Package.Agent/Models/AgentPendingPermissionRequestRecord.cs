using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Models;

public sealed record AgentPendingPermissionRequestRecord(
    string RequestId,
    Guid SessionId,
    Guid RunId,
    long RunRevision,
    string? ProfileId,
    Guid UserTurnId,
    string UserMessage,
    string CallId,
    string ActionId,
    string BoundaryId,
    string Summary,
    string? ToolId,
    string ArgumentsJson,
    string? Command,
    string? Path,
    string? WorkspaceId,
    string? BindingId,
    string? ResourceDisplayName,
    string? ResourceReference,
    bool IsMutation,
    DateTimeOffset CreatedAtUtc,
    Guid? ParentSessionId = null,
    Guid? RootSessionId = null);
