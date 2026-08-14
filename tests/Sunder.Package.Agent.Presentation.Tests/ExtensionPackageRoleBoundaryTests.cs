extern alias McpApp;
extern alias MemoryApp;
extern alias SkillsApp;
extern alias SubagentsApp;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Tests;
using Xunit;
using McpAppModule = McpApp::Sunder.Package.Agent.Mcp.AppPackageModule;
using MemoryAppModule = MemoryApp::Sunder.Package.Agent.Memory.Semantic.AppPackageModule;
using SkillsAppModule = SkillsApp::Sunder.Package.Agent.Skills.AppPackageModule;
using SubagentsAppModule = SubagentsApp::Sunder.Package.Agent.Subagents.AppPackageModule;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class ExtensionPackageRoleBoundaryTests
{
    [Fact]
    public void SemanticMemoryAppComposition_ContainsGatewayButNoWorkerAuthority()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();

        new MemoryAppModule().ConfigureAppServices(services, scope.Context);

        var implementationNames = GetImplementationTypeNames(services);
        Assert.Contains("MemoryAppRuntimeGateway", implementationNames);
        Assert.DoesNotContain("MemoryLocalStore", implementationNames);
        Assert.DoesNotContain("SemanticMemoryIndexingBackgroundService", implementationNames);
        Assert.DoesNotContain("MemorySemanticFeature", implementationNames);
    }

    [Theory]
    [MemberData(nameof(WorkerPackageModules))]
    public void WorkerAppComposition_ContainsGatewayButNoWorkerAuthority(
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext> configureApp,
        string gatewayTypeName,
        string[] forbiddenTypeNames)
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();

        configureApp(services, scope.Context);

        var implementationNames = GetImplementationTypeNames(services);
        Assert.Contains(gatewayTypeName, implementationNames);
        Assert.All(forbiddenTypeNames, forbidden => Assert.DoesNotContain(forbidden, implementationNames));
    }

    public static TheoryData<
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext>,
        string,
        string[]> WorkerPackageModules => new()
    {
        {
            (services, context) => new McpAppModule().ConfigureAppServices(services, context),
            "IMcpManagementGateway",
            ["McpServerCatalogService", "McpClientConnectionManager", "McpToolSource", "McpServerStackContributor"]
        },
        {
            (services, context) => new SkillsAppModule().ConfigureAppServices(services, context),
            "SkillAppRuntimeGateway",
            ["SkillStore", "SkillImportService", "SkillsFeature", "SkillStackContributor"]
        },
        {
            (services, context) => new SubagentsAppModule().ConfigureAppServices(services, context),
            "SubagentAppRuntimeGateway",
            ["SubagentStore", "SubagentService", "SubagentFeature", "OrchestratedAgentBehaviorLoop", "SubagentStackContributor"]
        },
    };

    private static HashSet<string> GetImplementationTypeNames(IServiceCollection services)
        => services
            .Select(descriptor => descriptor.ImplementationType?.Name
                ?? descriptor.ImplementationInstance?.GetType().Name
                ?? descriptor.ServiceType.Name)
            .ToHashSet(StringComparer.Ordinal);
}
