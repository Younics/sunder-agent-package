namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentSessionPermissionApproval(
    string ApprovalId,
    Guid SessionId,
    string ActionId,
    AgentPermissionMatcherKind MatcherKind,
    string Pattern,
    DateTimeOffset CreatedAtUtc);
