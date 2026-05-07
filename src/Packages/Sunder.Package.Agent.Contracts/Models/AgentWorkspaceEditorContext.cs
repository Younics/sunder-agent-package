namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspaceEditorContext(
    AgentWorkspaceRecord Workspace,
    string TargetId,
    string ConfigurationId);
