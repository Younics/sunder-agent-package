namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Represents a durable session-scoped approval used to satisfy matching ask-by-default permission requests.
/// </summary>
/// <remarks>
/// The permission evaluator may inherit an approval through the session ancestry. An approval does not bypass hard
/// denials, readiness checks, changed request context, or other host security validation.
/// </remarks>
/// <param name="ApprovalId">The stable identifier of the persisted approval.</param>
/// <param name="SessionId">The session at which the approval was granted.</param>
/// <param name="ActionId">The permission action to which the approval applies.</param>
/// <param name="MatcherKind">How <paramref name="Pattern"/> is compared with a later permission request.</param>
/// <param name="Pattern">The boundary or tool identifier interpreted according to <paramref name="MatcherKind"/>.</param>
/// <param name="CreatedAtUtc">The UTC time at which the user granted the approval.</param>
public sealed record AgentSessionPermissionApproval(
    string ApprovalId,
    Guid SessionId,
    string ActionId,
    AgentPermissionMatcherKind MatcherKind,
    string Pattern,
    DateTimeOffset CreatedAtUtc);
