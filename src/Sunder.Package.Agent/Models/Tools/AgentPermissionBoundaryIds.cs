namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Defines conventional resource boundary identifiers shared by permission planners and execution targets.</summary>
public static class AgentPermissionBoundaryIds
{
    /// <summary>The resource resolves within paths explicitly configured for the selected workspace.</summary>
    public const string ConfiguredScope = "configured-scope";
    /// <summary>The resource is outside configured paths and requires explicit authorization.</summary>
    public const string OutsideConfiguredScope = "outside-configured-scope";
    /// <summary>The operation runs inside the selected execution target.</summary>
    public const string SelectedExecutionTarget = "selected-execution-target";
    /// <summary>The resource could not be securely classified.</summary>
    public const string Unknown = "unknown";
}
