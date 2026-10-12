namespace Sunder.Package.Agent.Services;

[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public static class AgentWorkspaceBindingRoles
{
    public const string PrimaryExecutionTarget = "primary-execution-target";
    // Persisted workspace extension-point identity; it is not a client/dispatch registry.
    public const string ExecutionTargetExtensionPoint = "sunder.agent.execution.target";
}
