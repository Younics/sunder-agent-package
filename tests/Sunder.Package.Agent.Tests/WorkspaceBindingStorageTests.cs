extern alias DockerWorker;
extern alias LocalWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Sdk.Storage;
using Xunit;
using static Sunder.Package.Agent.Tests.WorkspaceStorageFixture;

namespace Sunder.Package.Agent.Tests;

[Collection("Workspace component behavior")]
public sealed class WorkspaceBindingStorageTests
{
    public static TheoryData<string> ImportedBindingIds => new()
    {
        "workspace:binding",
        "workspace / binding",
        "workspace-\u65E5\u672C\u8A9E-\u00E9",
        " workspace\twith whitespace ",
        new string('b', PackageStorageValidation.MaximumKeyLength + 300),
    };

    [Theory]
    [MemberData(nameof(ImportedBindingIds))]
    public async Task LocalWorkspaceConfig_UsesPortablePhysicalKeyForOpaqueBindingId(string bindingId)
    {
        using var scope = TestScope.Create();
        var service = new LocalExecutionWorkspaceConfigService(scope.Context);

        await service.SaveConfigAsync(bindingId, new LocalExecutionWorkspaceConfig("bash", []));
        var loaded = await service.GetConfigAsync(bindingId);

        Assert.Equal("bash", loaded.SelectedShellId);
        var key = Assert.Single(await scope.Context.Storage.State.ListKeysAsync());
        Assert.Equal(LocalExecutionWorkspaceConfigService.BuildKey(bindingId), key);
        Assert.True(PackageStorageValidation.IsValidKey(key));
    }

    [Theory]
    [MemberData(nameof(ImportedBindingIds))]
    public async Task DockerWorkspaceConfig_UsesPortablePhysicalKeyForOpaqueBindingId(string bindingId)
    {
        using var scope = TestScope.Create();
        var service = new DockerExecutionWorkspaceConfigService(scope.Context);

        await service.SaveConfigAsync(
            bindingId,
            new DockerExecutionWorkspaceConfig(null, null, "/bin/sh", []));
        var loaded = await service.GetConfigAsync(bindingId);

        Assert.Equal("/bin/sh", loaded.ShellPath);
        var keys = await scope.Context.Storage.State.ListKeysAsync();
        Assert.Contains(DockerExecutionWorkspaceConfigService.BuildKey(bindingId), keys);
        Assert.All(keys, key => Assert.True(PackageStorageValidation.IsValidKey(key)));
    }
}
