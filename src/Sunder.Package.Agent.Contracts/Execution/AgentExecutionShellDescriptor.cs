namespace Sunder.Package.Agent.Contracts.Models;

/// <summary>
/// Defines stable syntax discriminators understood by Agent shell consumers.
/// </summary>
/// <remarks>
/// A syntax kind describes command language and quoting expectations, not the identity or trustworthiness of a particular executable.
/// </remarks>
public static class AgentShellSyntaxKinds
{
    /// <summary>
    /// PowerShell command language, including PowerShell quoting, variables, and pipelines.
    /// </summary>
    public const string PowerShell = "powershell";

    /// <summary>
    /// Windows Command Prompt (<c>cmd.exe</c>) command language.
    /// </summary>
    public const string Cmd = "cmd";

    /// <summary>
    /// POSIX <c>sh</c>-compatible command language and path conventions.
    /// </summary>
    public const string PosixSh = "posix-sh";

    /// <summary>
    /// Target-specific syntax for which generic consumers must not assume standard quoting or path rules.
    /// </summary>
    public const string Custom = "custom";
}

/// <summary>
/// Describes the command interpreter selected for a workspace execution binding.
/// </summary>
/// <remarks>
/// The descriptor communicates syntax to callers but does not guarantee readiness, containment, or isolation. The executable remains owned
/// and launched by the execution target; consumers never acquire a process or stream from this record.
/// </remarks>
/// <param name="ShellId">A stable shell selection identity within the execution target; it need not be globally unique.</param>
/// <param name="DisplayName">The user-facing name of the selected interpreter.</param>
/// <param name="ExecutablePath">The interpreter path in the target's namespace, which may not be a usable host path.</param>
/// <param name="SyntaxKind">The command-language discriminator, normally one of the values in <see cref="AgentShellSyntaxKinds"/>.</param>
/// <param name="Description">User- and model-facing guidance about the interpreter and its expected command syntax.</param>
public sealed record AgentExecutionShellDescriptor(
    string ShellId,
    string DisplayName,
    string ExecutablePath,
    string SyntaxKind,
    string Description);
