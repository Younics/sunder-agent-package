extern alias LocalWorker;

using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Tests;
using Xunit;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using AgentModels = Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalWorkspaceConfigSafetyTests
{
    [Fact]
    public async Task MissingConfigurationRemainsAValidEmptyState()
    {
        using var scope = RegressionTestPackageScope.Create();
        var config = await new LocalExecutionWorkspaceConfigService(scope.Context)
            .GetConfigAsync("missing");

        Assert.Null(config.SelectedShellId);
        Assert.Empty(config.PathEntries ?? []);
    }

    [Theory]
    [InlineData("{ malformed", "local.workspace-config.malformed")]
    [InlineData("null", "local.workspace-config.malformed")]
    [InlineData("{\"PathEntries\":[1]}", "local.workspace-config.malformed")]
    [InlineData("{\"PathEntries\":[],\"pathentries\":[]}", "local.workspace-config.malformed")]
    [InlineData("{\"PathEntries\":[],\"futurePolicy\":true}", "local.workspace-config.unsupported-schema")]
    [InlineData("{\"AllowedRoots\":[\"/tmp\"]}", "local.workspace-config.unsupported-schema")]
    [InlineData("{\"DefaultWorkingDirectory\":\"/tmp\"}", "local.workspace-config.unsupported-schema")]
    public async Task MalformedOrUnsupportedConfigurationFailsClosedAndIsPreserved(
        string json,
        string expectedCode)
    {
        using var scope = RegressionTestPackageScope.Create();
        var state = Assert.IsType<RegressionTestKeyValueStore>(scope.Context.Storage.State);
        var key = LocalExecutionWorkspaceConfigService.BuildKey("corrupt");
        await state.SetValueAsync(key, json);
        var service = new LocalExecutionWorkspaceConfigService(scope.Context);

        var exception = await Assert.ThrowsAsync<LocalExecutionDomainException>(() =>
            service.GetConfigAsync("corrupt"));

        Assert.Equal(expectedCode, exception.Code);
        Assert.Equal(json, await state.GetValueAsync(key));
    }

    [Fact]
    public async Task CurrentConfigurationRoundTripsWithoutWorkspacePathMigration()
    {
        using var scope = RegressionTestPackageScope.Create();
        var service = new LocalExecutionWorkspaceConfigService(scope.Context);
        var path = Path.GetFullPath(Path.GetTempPath());
        await service.SaveConfigAsync("binding", new LocalExecutionWorkspaceConfig("custom-shell", [path]));

        var reloaded = await new LocalExecutionWorkspaceConfigService(scope.Context).GetConfigAsync("binding");
        Assert.Equal("custom-shell", reloaded.SelectedShellId);
        Assert.Equal(path, Assert.Single(reloaded.PathEntries!));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("{\"schemaVersion\":1,\"shells\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"revision\":0,\"shells\":[],\"Shells\":[]}")]
    public async Task ShellCatalogRejectsOldOrAmbiguousDocumentsWithoutOverwriting(string json)
    {
        using var scope = RegressionTestPackageScope.Create();
        await scope.Context.Storage.State.SetValueAsync("shells.custom", json);
        var catalog = new LocalShellCatalogService(scope.Context);

        await Assert.ThrowsAsync<LocalExecutionDomainException>(() => catalog.ListCustomShellsAsync());
        await Assert.ThrowsAsync<LocalExecutionDomainException>(() => catalog.SaveCustomShellsAsync([], 0));
        Assert.Equal(json, await scope.Context.Storage.State.GetValueAsync("shells.custom"));
    }

    [Fact]
    public async Task RestartedTargetSurfacesMalformedConfigurationThroughRpcReadiness()
    {
        using var scope = RegressionTestPackageScope.Create();
        var state = Assert.IsType<RegressionTestKeyValueStore>(scope.Context.Storage.State);
        const string bindingId = "workspace:primary-execution-target";
        const string json = "{ malformed";
        var key = LocalExecutionWorkspaceConfigService.BuildKey(bindingId);
        await state.SetValueAsync(key, json);

        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentModels.AgentWorkspaceRecord("workspace", "Workspace", null, now, now, []);
        var binding = new AgentModels.AgentWorkspaceBindingRecord(
            bindingId, workspace.WorkspaceId, "contract", "local", "primary", true, 0, now, now);
        var restartedTarget = new LocalExecutionTarget(
            scope.Context,
            new LocalExecutionWorkspaceConfigService(scope.Context),
            new LocalShellCatalogService(scope.Context));
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddGeneratedProvider(
            "younics.ai.agent.execution.local",
            restartedTarget,
            restartedTarget.CreateSunderRpcProviderRegistration());
        var provider = await catalog.GetRequiredProviderAsync(
            catalog.Receiver("younics.ai.agent").Providers<AgentServices.IExecutionTargetService>());

        var readiness = await provider.Service.ReadinessAsync(
            new AgentModels.AgentExecutionTargetContext(Guid.NewGuid(), null, workspace, binding));

        Assert.Equal(AgentModels.AgentExecutionTargetReadinessStatus.Failed, readiness.Status);
        Assert.Contains("local.workspace-config.malformed", readiness.Message, StringComparison.Ordinal);
        Assert.Equal(json, await state.GetValueAsync(key));
    }
}
