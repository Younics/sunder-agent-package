namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>Defines command-language and quoting conventions, not executable trust.</summary>
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public static class AgentShellSyntaxKinds
{
    /// <summary>PowerShell command language.</summary>
    public const string PowerShell = "powershell";
    /// <summary>Windows Command Prompt command language.</summary>
    public const string Cmd = "cmd";
    /// <summary>POSIX sh-compatible command language.</summary>
    public const string PosixSh = "posix-sh";
    /// <summary>Target-specific syntax with no generic quoting assumptions.</summary>
    public const string Custom = "custom";
}
