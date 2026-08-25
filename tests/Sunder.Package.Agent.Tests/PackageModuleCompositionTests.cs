using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class PackageModuleCompositionTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedRegistrations =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Sunder.Package.Agent"] =
            [
                PackageView("sunder.package.agent.chat", "Sunder.Package.Agent.PackageViews.AgentChatView"),
                PackageView("sunder.package.agent.history", "Sunder.Package.Agent.PackageViews.AgentHistorySearchView"),
                PackageView("sunder.package.agent.workspaces", "Sunder.Package.Agent.PackageViews.AgentWorkspacesView"),
                PackageView("sunder.package.agent.profiles", "Sunder.Package.Agent.PackageViews.AgentProfilesView"),
                SettingsView("Sunder.Package.Agent.PackageViews.AgentPermissionsView"),
            ],
            ["Sunder.Package.Agent.Builder"] =
            [
                PackageView("sunder.package.agent.builder", "Sunder.Package.Agent.Builder.BuilderView"),
            ],
            ["Sunder.Package.Agent.Execution.Docker"] =
            [
                SettingsView("Sunder.Package.Agent.Execution.Docker.DockerExecutionSettingsView"),
            ],
            ["Sunder.Package.Agent.Execution.Local"] =
            [
                SettingsView("Sunder.Package.Agent.Execution.Local.LocalExecutionSettingsView"),
            ],
            ["Sunder.Package.Agent.Mcp"] =
            [
                SettingsView("Sunder.Package.Agent.Mcp.AgentMcpSettingsView"),
            ],
            ["Sunder.Package.Agent.Memory.Semantic"] =
            [
                PackageView("sunder.package.agent.memory.semantic.inspector", "Sunder.Package.Agent.Memory.Semantic.PackageViews.MemoryInspectorView"),
            ],
            ["Sunder.Package.Agent.Provider.Anthropic"] =
            [
                SettingsView("Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsView"),
            ],
            ["Sunder.Package.Agent.Provider.Gemini"] =
            [
                SettingsView("Sunder.Package.Agent.Provider.Gemini.GeminiSettingsView"),
            ],
            ["Sunder.Package.Agent.Provider.LMStudio"] =
            [
                SettingsView("Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsView"),
            ],
            ["Sunder.Package.Agent.Provider.OpenAI"] =
            [
                SettingsView("Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsView"),
            ],
            ["Sunder.Package.Agent.Skills"] =
            [
                SettingsView("Sunder.Package.Agent.Skills.PackageViews.SkillSettingsView"),
            ],
            ["Sunder.Package.Agent.Subagents"] =
            [
                PackageView("sunder.package.agent.subagents.sessions", "Sunder.Package.Agent.Subagents.PackageViews.SubsessionsView"),
                PackageView("sunder.package.agent.subagents", "Sunder.Package.Agent.Subagents.PackageViews.SubagentsView"),
            ],
        };

    public static IEnumerable<object[]> AppPackageNames
        => AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Where(static package => package.AppProjectPath is not null)
            .Select(static package => new object[] { package.Name });

    [Theory]
    [MemberData(nameof(AppPackageNames))]
    [Trait("Lane", "PackageCompatibility")]
    public async Task AppPackageModule_ConfiguresValidatedServicesAndRegistersContributions(string packageName)
    {
        using var packageScope = RegressionTestPackageScope.Create();
        using var rpcState = new RegressionTestRpcState();
        var extensionCatalog = new RegressionTestExtensionCatalog();
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(packageScope.Context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(extensionCatalog);
        services.AddSingleton<Sunder.Sdk.Rpc.ISunderRpcClient>(
            new RegressionTestRpcClient(rpcState, packageName));
        services.AddSingleton<IBackgroundProcessQueue, CompositionBackgroundProcessQueue>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);

        var appModule = CreateAppPackageModule(packageName);
        appModule.ConfigureAppServices(services, packageScope.Context);

        await using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var registry = new RecordingPackageContributionRegistry();

        appModule.RegisterAppContributions(registry, serviceProvider);

        Assert.Equal(
            ExpectedRegistrations[packageName].Order(StringComparer.Ordinal),
            registry.Registrations.Order(StringComparer.Ordinal));

    }

    [Theory]
    [MemberData(nameof(AppPackageNames))]
    public void AppPackageModule_DoesNotRegisterHostOwnedPackageContext(string packageName)
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();

        CreateAppPackageModule(packageName).ConfigureAppServices(services, packageScope.Context);

        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IPackageContext));
    }

    [Fact]
    public void ExactContributionBaseline_CoversEveryRuntimePackage()
    {
        Assert.Equal(
            AgentPackageRepositoryInventory.GetRuntimePackageProjects()
                .Where(static package => package.AppProjectPath is not null)
                .Select(static package => package.Name),
            ExpectedRegistrations.Keys.Order(StringComparer.Ordinal));
    }

    private static ISunderAppPackageModule CreateAppPackageModule(string packageName)
    {
        var package = Assert.Single(
            AgentPackageRepositoryInventory.GetRuntimePackageProjects(),
            package => string.Equals(package.Name, packageName, StringComparison.Ordinal));
        Assert.NotNull(package.AppAssemblyName);
        var assembly = Assembly.Load(package.AppAssemblyName!);
        var appTypes = assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(ISunderAppPackageModule).IsAssignableFrom(type))
            .ToArray();

        var appType = Assert.Single(appTypes);
        return Assert.IsAssignableFrom<ISunderAppPackageModule>(Activator.CreateInstance(appType));
    }

    private static string PackageView(string id, string implementationType)
        => $"package-view:{id}:{implementationType}";

    private static string SettingsView(string implementationType)
        => $"settings-view:{implementationType}";

}
