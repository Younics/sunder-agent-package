extern alias FilesWorker;

using FilesWorker::Sunder.Package.Agent.Tools.Files;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class ToolPackageRoleCompositionTests
{
    private const string PackageAssemblyName = "Sunder.Package.Agent.Tools.Files";
    private static string[] ExpectedRegistrations =>
        ["rpc-provider:files.tools", "rpc-provider:files.permissions", "rpc-provider:files.prompt.context", "rpc-provider:files.session.cleaner"];

    [Fact]
    public async Task FilesComposition_DefersDurableWorkspaceCreationUntilActivation()
    {
        using var scope = RegressionTestPackageScope.Create();
        var claimsRoot = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath("files/scoped-instruction-claims");
        var source = new FilesToolSource(scope.Context);
        Assert.False(Directory.Exists(claimsRoot));
        await source.ActivateAsync(default!);
        Assert.True(Directory.Exists(claimsRoot));
    }
}
