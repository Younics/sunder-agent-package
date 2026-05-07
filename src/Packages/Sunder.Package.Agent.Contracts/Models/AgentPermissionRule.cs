namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentPermissionRule(
    string RuleId,
    string ActionId,
    AgentPermissionMatcherKind MatcherKind,
    string Pattern,
    AgentPermissionDecision Decision,
    int SortOrder);
