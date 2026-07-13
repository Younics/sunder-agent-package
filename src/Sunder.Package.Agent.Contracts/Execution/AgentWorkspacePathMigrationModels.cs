namespace Sunder.Package.Agent.Contracts.Models;

public sealed record AgentWorkspacePathMigrationContext(
    AgentWorkspaceRecord Workspace,
    AgentWorkspaceBindingRecord Binding);

public sealed record AgentWorkspacePathMigrationItem(
    string HostPath,
    bool IsDefault = false,
    int SortOrder = 0);
