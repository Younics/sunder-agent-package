namespace Sunder.Package.Agent.Contracts.Models;

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
[Sunder.Sdk.Packaging.SunderPackageCodeRole(Sunder.Sdk.Packaging.SunderPackageCodeRole.Shared)]
public sealed record AgentExecutionShellDescriptor(
    string ShellId,
    string DisplayName,
    string ExecutablePath,
    string SyntaxKind,
    string Description);
