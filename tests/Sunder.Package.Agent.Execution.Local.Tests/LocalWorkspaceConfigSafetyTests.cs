using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tests;
using Xunit;

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
    public async Task MalformedLegacyDiscoveryFailsInsteadOfSilentlyReturningNoPaths()
    {
        using var scope = RegressionTestPackageScope.Create();
        var state = Assert.IsType<RegressionTestKeyValueStore>(scope.Context.Storage.State);
        var binding = new Sunder.Package.Agent.Contracts.Models.AgentWorkspaceBindingRecord(
            "binding",
            "workspace",
            "contract",
            "local",
            "primary",
            true,
            0,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var key = LocalExecutionWorkspaceConfigService.BuildKey(binding.BindingId);
        const string json = "{\"AllowedRoots\":[42]}";
        await state.SetValueAsync(key, json);
        var service = new LocalExecutionWorkspaceConfigService(scope.Context);
        var workspace = new Sunder.Package.Agent.Contracts.Models.AgentWorkspaceRecord(
            "workspace",
            "Workspace",
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var context = new Sunder.Package.Agent.Contracts.Models.AgentWorkspacePathMigrationContext(
            workspace,
            binding);

        var exception = await Assert.ThrowsAsync<LocalExecutionDomainException>(() =>
            service.GetLegacyWorkspacePathsAsync(context));

        Assert.Equal("local.workspace-config.malformed", exception.Code);
        Assert.Equal(json, await state.GetValueAsync(key));
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
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var binding = new AgentWorkspaceBindingRecord(
            bindingId,
            workspace.WorkspaceId,
            "contract",
            "local",
            "primary",
            true,
            0,
            now,
            now);
        var restartedTarget = new LocalExecutionTarget(
            scope.Context,
            new LocalExecutionWorkspaceConfigService(scope.Context),
            new LocalShellCatalogService(scope.Context));
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(
            AgentRpcServices.ExecutionTargets,
            restartedTarget,
            "sunder.package.agent.execution.local");
        var reference = catalog.GetRequiredReference(AgentRpcServices.ExecutionTargets);
        Assert.True(reference.TryAcquire(out var lease));

        AgentExecutionTargetReadiness readiness;
        using (lease)
        {
            readiness = await lease.Service.GetReadinessAsync(
                new AgentExecutionTargetContext(null, null, workspace, binding));
        }

        Assert.Equal(AgentExecutionTargetReadinessStatus.Failed, readiness.Status);
        Assert.Contains("local.workspace-config.malformed", readiness.Message, StringComparison.Ordinal);
        Assert.Equal(json, await state.GetValueAsync(key));
    }
}
