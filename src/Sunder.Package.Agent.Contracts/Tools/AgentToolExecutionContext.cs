namespace Sunder.Package.Agent.Contracts.Models;

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
