namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Carries host-verified resource scope and durable-run identity into tool execution and permission planning.
/// </summary>
/// <remarks>
/// Context records are host-owned snapshots and should not be retained or modified. Correlation
/// identifiers are not authorization by themselves. Implementations must still validate tool
/// arguments and re-resolve resources at execution time.
/// </remarks>
/// <param name="SessionId">The active session identifier, or <see langword="null" /> outside a durable session.</param>
/// <param name="ProfileId">The selected profile identifier, or <see langword="null" /> when no profile is in scope.</param>
/// <param name="Workspace">The selected workspace snapshot, or <see langword="null" /> for non-workspace tools.</param>
/// <param name="ExecutionBinding">The selected workspace execution binding, or <see langword="null" /> when none applies.</param>
/// <param name="AllowOutsideConfiguredScope">Whether the host has approved access outside configured workspace paths for this invocation; targets must still resolve and enforce the approved boundary.</param>
/// <param name="RunId">The durable run identifier used for correlation and approval binding.</param>
/// <param name="RunRevision">The durable run revision that must remain current for execution.</param>
/// <param name="UserTurnId">The user turn that initiated the provider run.</param>
/// <param name="ToolCallId">The provider's call identifier used to correlate approval and result records.</param>
public sealed record AgentToolExecutionContext(
    Guid? SessionId,
    string? ProfileId = null,
    AgentWorkspaceRecord? Workspace = null,
    AgentWorkspaceBindingRecord? ExecutionBinding = null,
    bool AllowOutsideConfiguredScope = false,
    Guid? RunId = null,
    long? RunRevision = null,
    Guid? UserTurnId = null,
    string? ToolCallId = null);
