using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Tests;
using Xunit;
using McpAppModule = Sunder.Package.Agent.Mcp.AppPackageModule;
using McpRuntimeModule = Sunder.Package.Agent.Mcp.PackageModule;
using MemoryAppModule = Sunder.Package.Agent.Memory.Semantic.AppPackageModule;
using MemoryRuntimeModule = Sunder.Package.Agent.Memory.Semantic.PackageModule;
using SkillsAppModule = Sunder.Package.Agent.Skills.AppPackageModule;
using SkillsRuntimeModule = Sunder.Package.Agent.Skills.PackageModule;
using SubagentsAppModule = Sunder.Package.Agent.Subagents.AppPackageModule;
using SubagentsRuntimeModule = Sunder.Package.Agent.Subagents.PackageModule;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class ExtensionPackageRoleBoundaryTests
{
    [Theory]
    [MemberData(nameof(PackageModules))]
    public void AppComposition_ContainsPresentationGatewayButNoRuntimeAuthority(
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext> configureApp,
        string gatewayTypeName,
        string[] forbiddenTypeNames,
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext> configureRuntime)
    {
        _ = configureRuntime;
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        services.AddSingleton<Sunder.Sdk.Runtime.IPackageRuntimeClient>(
            Sunder.Sdk.Runtime.NullPackageRuntimeClient.Instance);

        configureApp(services, scope.Context);
        var runtimeServices = new ServiceCollection();
        configureRuntime(runtimeServices, scope.Context);

        var implementationNames = GetImplementationTypeNames(services);
        Assert.Contains(gatewayTypeName, implementationNames);
        using var provider = services.BuildServiceProvider();
        foreach (var forbidden in forbiddenTypeNames)
        {
            Assert.DoesNotContain(forbidden, implementationNames);
            var forbiddenTypes = runtimeServices
                .SelectMany(descriptor => new[] { descriptor.ServiceType, descriptor.ImplementationType })
                .OfType<Type>()
                .Where(type => string.Equals(type.Name, forbidden, StringComparison.Ordinal))
                .Distinct()
                .ToArray();
            Assert.NotEmpty(forbiddenTypes);
            Assert.All(forbiddenTypes, type => Assert.Null(provider.GetService(type)));
        }
    }

    [Theory]
    [MemberData(nameof(PackageModules))]
    public void RuntimeComposition_RetainsPackageAuthority(
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext> configureApp,
        string gatewayTypeName,
        string[] forbiddenTypeNames,
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext> configureRuntime)
    {
        _ = configureApp;
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();

        configureRuntime(services, scope.Context);

        var implementationNames = GetImplementationTypeNames(services);
        Assert.DoesNotContain(gatewayTypeName, implementationNames);
        Assert.All(forbiddenTypeNames, forbidden => Assert.Contains(forbidden, implementationNames));
    }

    public static TheoryData<
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext>,
        string,
        string[],
        Action<IServiceCollection, Sunder.Sdk.Abstractions.IPackageContext>> PackageModules => new()
    {
        {
            (services, context) => new McpAppModule().ConfigureAppServices(services, context),
            "McpAppRuntimeGateway",
            ["McpServerCatalogService", "McpClientConnectionManager", "McpToolSource", "McpServerStackContributor"],
            (services, context) => new McpRuntimeModule().ConfigureRuntimeServices(services, context)
        },
        {
            (services, context) => new SkillsAppModule().ConfigureAppServices(services, context),
            "SkillAppRuntimeGateway",
            ["SkillStore", "SkillImportService", "SkillsFeature", "SkillStackContributor"],
            (services, context) => new SkillsRuntimeModule().ConfigureRuntimeServices(services, context)
        },
        {
            (services, context) => new SubagentsAppModule().ConfigureAppServices(services, context),
            "SubagentAppRuntimeGateway",
            ["SubagentStore", "SubagentService", "SubagentFeature", "OrchestratedAgentBehaviorLoop", "SubagentStackContributor"],
            (services, context) => new SubagentsRuntimeModule().ConfigureRuntimeServices(services, context)
        },
        {
            (services, context) => new MemoryAppModule().ConfigureAppServices(services, context),
            "MemoryAppRuntimeGateway",
            ["MemoryLocalStore", "SemanticMemoryIndexingBackgroundService", "MemorySemanticFeature"],
            (services, context) => new MemoryRuntimeModule().ConfigureRuntimeServices(services, context)
        },
    };

    private static HashSet<string> GetImplementationTypeNames(IServiceCollection services)
        => services
            .Select(descriptor => descriptor.ImplementationType?.Name
                ?? descriptor.ImplementationInstance?.GetType().Name
                ?? descriptor.ServiceType.Name)
            .ToHashSet(StringComparer.Ordinal);
}
