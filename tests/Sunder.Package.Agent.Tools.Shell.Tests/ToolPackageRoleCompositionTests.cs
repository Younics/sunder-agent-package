namespace Sunder.Package.Agent.Tests;

public sealed partial class ToolPackageRoleCompositionTests
{
    private const string PackageAssemblyName = "Sunder.Package.Agent.Tools.Shell";
    private static string[] ExpectedRegistrations =>
        ["rpc-provider:shell.tools", "rpc-provider:shell.permissions", "rpc-provider:shell.prompt.context"];
}
