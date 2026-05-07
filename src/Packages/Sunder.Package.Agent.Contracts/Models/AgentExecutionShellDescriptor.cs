namespace Sunder.Package.Agent.Contracts.Models;

public static class AgentShellSyntaxKinds
{
    public const string PowerShell = "powershell";
    public const string Cmd = "cmd";
    public const string PosixSh = "posix-sh";
    public const string Custom = "custom";
}

public sealed record AgentExecutionShellDescriptor(
    string ShellId,
    string DisplayName,
    string ExecutablePath,
    string SyntaxKind,
    string Description);
