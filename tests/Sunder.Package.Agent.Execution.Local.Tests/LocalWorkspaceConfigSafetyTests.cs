extern alias LocalWorker;

using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Rpc;
using Xunit;
using LocalModels = LocalWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Models;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
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
        var now = new SunderRpcOffsetDateTime(DateTimeOffset.UtcNow.ToString("O"));
        var binding = new LocalModels.AgentWorkspaceBindingRecord
        {
            BindingId = "binding",
            WorkspaceId = "workspace",
            ExtensionPointId = "contract",
            ContributionId = "local",
            Role = "primary",
            IsEnabled = true,
            SortOrder = 0,
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
        };
        var key = LocalExecutionWorkspaceConfigService.BuildKey(binding.BindingId);
        const string json = "{\"AllowedRoots\":[42]}";
        await state.SetValueAsync(key, json);
        var service = new LocalExecutionWorkspaceConfigService(scope.Context);
        var workspace = new LocalModels.AgentWorkspaceRecord
        {
            WorkspaceId = "workspace",
            DisplayName = "Workspace",
            CreatedAtUtc = now,
            UpdatedAtUtc = now,
            Paths = [],
            Documents = [],
        };
        var context = new LocalModels.AgentWorkspacePathMigrationContext
        {
            Workspace = workspace,
            Binding = binding,
        };

        var exception = await Assert.ThrowsAsync<LocalExecutionDomainException>(() =>
            service.ListLegacyPathsAsync(context).AsTask());

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
        var workspace = new AgentModels.AgentWorkspaceRecord("workspace", "Workspace", null, now, now, []);
        var binding = new AgentModels.AgentWorkspaceBindingRecord(
            bindingId, workspace.WorkspaceId, "contract", "local", "primary", true, 0, now, now);
        var restartedTarget = new LocalExecutionTarget(
            scope.Context,
            new LocalExecutionWorkspaceConfigService(scope.Context),
            new LocalShellCatalogService(scope.Context));
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddGeneratedProvider(
            "sunder.package.agent.execution.local",
            restartedTarget,
            restartedTarget.CreateSunderRpcProviderRegistration());
        var provider = await catalog.GetRequiredProviderAsync(
            catalog.Receiver("sunder.package.agent").Providers<AgentServices.IExecutionTargetService>());

        var readiness = await provider.Service.ReadinessAsync(
            new AgentModels.AgentExecutionTargetContext(Guid.NewGuid(), null, workspace, binding));

        Assert.Equal(AgentModels.AgentExecutionTargetReadinessStatus.Failed, readiness.Status);
        Assert.Contains("local.workspace-config.malformed", readiness.Message, StringComparison.Ordinal);
        Assert.Equal(json, await state.GetValueAsync(key));
    }
}
