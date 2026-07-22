namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Reports whether a tool can be advertised in the context for which it was checked.
/// </summary>
/// <param name="ToolId">The canonical tool identifier to which the report applies.</param>
/// <param name="Status">The point-in-time catalog eligibility state.</param>
/// <param name="Message">A user-facing explanation that must not contain secrets or authorization tokens.</param>
public sealed record AgentToolReadiness(
    string ToolId,
    AgentToolReadinessStatus Status,
    string Message);
