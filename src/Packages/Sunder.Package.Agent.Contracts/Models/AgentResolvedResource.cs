namespace Sunder.Package.Agent.Contracts.Models;

public static class AgentPermissionBoundaryIds
{
    public const string ConfiguredScope = "configured-scope";
    public const string OutsideConfiguredScope = "outside-configured-scope";
    public const string SelectedExecutionTarget = "selected-execution-target";
    public const string Unknown = "unknown";
}

public sealed record AgentResolvedResource(
    string ResourceKind,
    string DisplayName,
    string CanonicalReference,
    string PermissionBoundaryId,
    bool Exists);
