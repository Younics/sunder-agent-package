using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class PackageModuleCompositionTests
{
    private static readonly IReadOnlyDictionary<string, string[]> ExpectedRegistrations =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            ["Sunder.Package.Agent"] =
            [
                BackgroundService("Sunder.Package.Agent.Services.AgentPackageStorageMigration"),
                BackgroundService("Sunder.Package.Agent.Services.AgentRuntimeStartupService"),
                RpcProvider("agent.runtime.catalog"),
                RpcProvider("agent.workspace.execution.resolver"),
                RpcProvider("agent.child.runs"),
                RpcProvider("agent.attachment.cleaner"),
                RpcProvider("agent.history.cleaner"),
                RpcProvider("agent.run.control"),
                RpcProvider("agent.behavior.loop"),
                RpcProvider("agent.workspace.documentation"),
                RpcProvider("agent.stack.profiles"),
                RpcProvider("agent.stack.workspaces"),
                RuntimeOperation("agent.chat.snapshot.v1", "Sunder.Package.Agent.Runtime.AgentChatSnapshotHandler"),
                RuntimeOperation("agent.dashboard.v1", "Sunder.Package.Agent.Runtime.AgentDashboardHandler"),
                RuntimeOperation("agent.transcript.page.v1", "Sunder.Package.Agent.Runtime.AgentTranscriptPageHandler"),
                RuntimeOperation("agent.transcript.tool-detail.v1", "Sunder.Package.Agent.Runtime.AgentTranscriptToolDetailHandler"),
                RuntimeOperation("agent.catalog.v1", "Sunder.Package.Agent.Runtime.AgentCatalogHandler"),
                RuntimeOperation("agent.profiles.command.v1", "Sunder.Package.Agent.Runtime.AgentProfileCommandHandler"),
                RuntimeOperation("agent.workspaces.command.v1", "Sunder.Package.Agent.Runtime.AgentWorkspaceCommandHandler"),
                RuntimeOperation("agent.sessions.command.v1", "Sunder.Package.Agent.Runtime.AgentSessionCommandHandler"),
                RuntimeOperation("agent.runs.command.v1", "Sunder.Package.Agent.Runtime.AgentRunCommandHandler"),
                RuntimeOperation("agent.runs.status.v1", "Sunder.Package.Agent.Runtime.AgentRunCommandHandler"),
                RuntimeOperation("agent.permissions.command.v1", "Sunder.Package.Agent.Runtime.AgentPermissionCommandHandler"),
                RuntimeOperation("agent.attachments.transfer.v1", "Sunder.Package.Agent.Runtime.AgentAttachmentTransferHandler"),
                RuntimeOperation("agent.history.search.v1", "Sunder.Package.Agent.Runtime.AgentHistorySearchHandler"),
                RuntimeOperation("agent.history.state.v1", "Sunder.Package.Agent.Runtime.AgentHistoryStateHandler"),
                RuntimeOperation("agent.history.command.v1", "Sunder.Package.Agent.Runtime.AgentHistoryCommandHandler"),
                RuntimeOperation("agent.transcript.around.v1", "Sunder.Package.Agent.Runtime.AgentTranscriptAroundTurnHandler"),
                RuntimeStream("agent.changes.v1", "Sunder.Package.Agent.Runtime.AgentRuntimeChangeHub"),
                RuntimeStream("agent.history.status.v1", "Sunder.Package.Agent.Runtime.AgentHistoryStatusStream"),
                PackageView("sunder.package.agent.chat", "Sunder.Package.Agent.PackageViews.AgentChatView"),
                PackageView("sunder.package.agent.history", "Sunder.Package.Agent.PackageViews.AgentHistorySearchView"),
                PackageView("sunder.package.agent.workspaces", "Sunder.Package.Agent.PackageViews.AgentWorkspacesView"),
                PackageView("sunder.package.agent.profiles", "Sunder.Package.Agent.PackageViews.AgentProfilesView"),
                SettingsView("Sunder.Package.Agent.PackageViews.AgentPermissionsView"),
            ],
            ["Sunder.Package.Agent.Builder"] =
            [
                PackageView("sunder.package.agent.builder", "Sunder.Package.Agent.Builder.BuilderView"),
                RuntimeOperation("agent.builder.execute.v1", "Sunder.Package.Agent.Builder.BuilderRuntimeHandler"),
            ],
            ["Sunder.Package.Agent.Execution.Docker"] =
            [
                BackgroundService("Sunder.Package.Agent.Execution.Docker.DockerPackageStorageMigration"),
                Configuration("sunder.package.agent.execution.docker"),
                RpcProvider("docker.stack"),
                SettingsView("Sunder.Package.Agent.Execution.Docker.DockerExecutionSettingsView"),
                RpcProvider("docker.execution.target"),
                RpcProvider("docker.workspace.path.migrator"),
                RpcProvider("docker.workspace.editor"),
                RuntimeOperation("docker-execution.presentation.v1", "Sunder.Package.Agent.Execution.Docker.DockerExecutionRuntimeOperationHandler"),
            ],
            ["Sunder.Package.Agent.Execution.Local"] =
            [
                BackgroundService("Sunder.Package.Agent.Execution.Local.LocalPackageStorageMigration"),
                Configuration("sunder.package.agent.execution.local"),
                SettingsView("Sunder.Package.Agent.Execution.Local.LocalExecutionSettingsView"),
                RpcProvider("local.execution.target"),
                RpcProvider("local.workspace.path.migrator"),
                RpcProvider("local.workspace.editor"),
                RuntimeOperation("local-execution.presentation.v1", "Sunder.Package.Agent.Execution.Local.LocalExecutionRuntimeOperationHandler"),
            ],
            ["Sunder.Package.Agent.Mcp"] =
            [
                BackgroundService("Sunder.Package.Agent.Mcp.Services.McpPackageRuntimeStartupService"),
                SettingsView("Sunder.Package.Agent.Mcp.AgentMcpSettingsView"),
                RpcProvider("mcp.tools"),
                RpcProvider("mcp.selectable.capabilities"),
                RpcProvider("mcp.session.cleaner"),
                RpcProvider("mcp.stack"),
                RuntimeOperation("mcp.query.v1", "Sunder.Package.Agent.Mcp.Runtime.McpRuntimeHandler"),
                RuntimeOperation("mcp.command.v1", "Sunder.Package.Agent.Mcp.Runtime.McpRuntimeHandler"),
                RuntimeStream("mcp.changes.v1", "Sunder.Package.Agent.Mcp.Runtime.McpRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Memory.Semantic"] =
            [
                Configuration("sunder.package.agent.memory.semantic"),
                BackgroundService("Sunder.Package.Agent.Memory.Semantic.Services.SemanticMemoryIndexingBackgroundService"),
                RpcProvider("semantic.memory.prompt.context"),
                RpcProvider("semantic.memory.lifecycle"),
                RpcProvider("semantic.memory.profile.capabilities"),
                RpcProvider("semantic.memory.session.cleaner"),
                PackageView("sunder.package.agent.memory.semantic.inspector", "Sunder.Package.Agent.Memory.Semantic.PackageViews.MemoryInspectorView"),
                RuntimeOperation("semantic-memory.query.v1", "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryRuntimeHandler"),
                RuntimeOperation("semantic-memory.command.v1", "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryRuntimeHandler"),
                RuntimeStream("semantic-memory.changes.v1", "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Provider.Anthropic"] =
            [
                Configuration("sunder.package.agent.provider.anthropic"),
                SettingsView("Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsView"),
                RpcProvider("anthropic.chat"),
            ],
            ["Sunder.Package.Agent.Provider.Gemini"] =
            [
                Configuration("sunder.package.agent.provider.gemini"),
                SettingsView("Sunder.Package.Agent.Provider.Gemini.GeminiSettingsView"),
                RpcProvider("gemini.chat"),
                RpcProvider("gemini.embedding"),
            ],
            ["Sunder.Package.Agent.Provider.LMStudio"] =
            [
                Configuration("sunder.package.agent.provider.lmstudio"),
                SettingsView("Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsView"),
                RpcProvider("lmstudio.chat"),
                RpcProvider("lmstudio.embedding"),
            ],
            ["Sunder.Package.Agent.Provider.OpenAI"] =
            [
                Configuration("sunder.package.agent.provider.openai"),
                SettingsView("Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsView"),
                RpcProvider("openai.chat"),
                RpcProvider("openai.embedding"),
                RpcProvider("openai.continuation.cleaner"),
                RuntimeOperation("openai.auth.v1", "Sunder.Package.Agent.Provider.OpenAI.OpenAiAuthOperationHandler"),
            ],
            ["Sunder.Package.Agent.Skills"] =
            [
                SettingsView("Sunder.Package.Agent.Skills.PackageViews.SkillSettingsView"),
                RpcProvider("skills.selectable.capabilities"),
                RpcProvider("skills.tools"),
                RpcProvider("skills.prompt.context"),
                RpcProvider("skills.stack"),
                RuntimeOperation("skills.query.v1", "Sunder.Package.Agent.Skills.Runtime.SkillRuntimeHandler"),
                RuntimeOperation("skills.command.v1", "Sunder.Package.Agent.Skills.Runtime.SkillRuntimeHandler"),
                RuntimeStream("skills.changes.v1", "Sunder.Package.Agent.Skills.Runtime.SkillRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Subagents"] =
            [
                PackageView("sunder.package.agent.subagents.sessions", "Sunder.Package.Agent.Subagents.PackageViews.SubsessionsView"),
                PackageView("sunder.package.agent.subagents", "Sunder.Package.Agent.Subagents.PackageViews.SubagentsView"),
                RpcProvider("subagents.selectable.capabilities"),
                RpcProvider("subagents.tools"),
                RpcProvider("subagents.prompt.context"),
                RpcProvider("subagents.behavior.loop"),
                RpcProvider("subagents.stack"),
                RuntimeOperation("subagents.query.v1", "Sunder.Package.Agent.Subagents.Runtime.SubagentRuntimeHandler"),
                RuntimeOperation("subagents.command.v1", "Sunder.Package.Agent.Subagents.Runtime.SubagentRuntimeHandler"),
                RuntimeStream("subagents.changes.v1", "Sunder.Package.Agent.Subagents.Runtime.SubagentRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Tools.Files"] =
            [
                RpcProvider("files.tools"),
                RpcProvider("files.permissions"),
                RpcProvider("files.prompt.context"),
                RpcProvider("files.session.cleaner"),
            ],
            ["Sunder.Package.Agent.Tools.Shell"] =
            [
                RpcProvider("shell.tools"),
                RpcProvider("shell.permissions"),
                RpcProvider("shell.prompt.context"),
            ],
            ["Sunder.Package.Agent.Tools.Web"] =
            [
                Configuration("sunder.package.agent.tools.web"),
                RpcProvider("web.tools"),
            ],
        };

    public static IEnumerable<object[]> RuntimePackageNames
        => AgentPackageRepositoryInventory.GetRuntimePackageProjects()
            .Select(static package => new object[] { package.Name });

    [Theory]
    [MemberData(nameof(RuntimePackageNames))]
    public async Task PackageModule_ConfiguresValidatedServicesAndRegistersContributions(string packageName)
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var extensionCatalog = new RegressionTestExtensionCatalog();
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(packageScope.Context);
        services.AddSingleton<Sunder.Package.Agent.Protocol.AgentRpcCatalog>(extensionCatalog);
        services.AddSingleton<Sunder.Sdk.Runtime.IPackageRuntimeClient>(Sunder.Sdk.Runtime.NullPackageRuntimeClient.Instance);
        services.AddSingleton<Sunder.Sdk.Rpc.ISunderRpcContentClient>(TestRpcContentClient.Instance);
        services.AddSingleton<IBackgroundProcessQueue, CompositionBackgroundProcessQueue>();

        var (runtimeModule, appModule) = CreatePackageModules(packageName);
        if (runtimeModule is not null)
        {
            runtimeModule.ConfigureRuntimeServices(services, packageScope.Context);
            appModule?.ConfigureAppServices(services, packageScope.Context);
        }
        else
        {
            Assert.NotNull(appModule);
            appModule.ConfigureAppServices(services, packageScope.Context);
        }

        await using var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var registry = new RecordingPackageContributionRegistry();

        if (runtimeModule is not null)
        {
            runtimeModule.RegisterRuntimeContributions(registry, serviceProvider);
        }
        if (appModule is not null)
        {
            appModule.RegisterAppContributions(registry, serviceProvider);
        }

        Assert.Equal(
            ExpectedRegistrations[packageName].Order(StringComparer.Ordinal),
            registry.Registrations.Order(StringComparer.Ordinal));

        if (string.Equals(packageName, "Sunder.Package.Agent", StringComparison.Ordinal))
        {
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentRunPreparationService>());
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentRunStartService>());
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentRunExecutionService>());
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentUserMessageRunCoordinator>());
        }
        if (string.Equals(packageName, "Sunder.Package.Agent.Tools.Files", StringComparison.Ordinal))
        {
            Assert.True(serviceProvider
                .GetRequiredService<Sunder.Package.Agent.Tools.Files.FilesToolSource>()
                .IsScopedInstructionEnforcementEnabled);
        }
    }

    [Fact]
    public void ExactContributionBaseline_CoversEveryRuntimePackage()
    {
        Assert.Equal(
            AgentPackageRepositoryInventory.GetRuntimePackageProjects().Select(static package => package.Name),
            ExpectedRegistrations.Keys.Order(StringComparer.Ordinal));
    }

    private static (ISunderRuntimePackageModule? Runtime, ISunderAppPackageModule? App) CreatePackageModules(string packageName)
    {
        var assembly = Assembly.Load(packageName);
        var runtimeTypes = assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(ISunderRuntimePackageModule).IsAssignableFrom(type))
            .ToArray();
        var appTypes = assembly.GetTypes()
            .Where(static type => !type.IsAbstract && typeof(ISunderAppPackageModule).IsAssignableFrom(type))
            .ToArray();

        Assert.True(runtimeTypes.Length + appTypes.Length > 0);
        Assert.True(runtimeTypes.Length <= 1);
        Assert.True(appTypes.Length <= 1);
        return (
            runtimeTypes.Length == 0 ? null : Assert.IsAssignableFrom<ISunderRuntimePackageModule>(Activator.CreateInstance(runtimeTypes[0])),
            appTypes.Length == 0 ? null : Assert.IsAssignableFrom<ISunderAppPackageModule>(Activator.CreateInstance(appTypes[0])));
    }

    private static string RpcProvider(string providerId) => $"rpc-provider:{providerId}";

    private static string PackageView(string id, string implementationType)
        => $"package-view:{id}:{implementationType}";

    private static string SettingsView(string implementationType)
        => $"settings-view:{implementationType}";

    private static string BackgroundService(string implementationType)
        => $"background-service:{implementationType}";

    private static string RuntimeOperation(string id, string implementationType)
        => $"runtime-operation:{id}:{implementationType}";

    private static string RuntimeStream(string id, string implementationType)
        => $"runtime-stream:{id}:{implementationType}";

    private static string Configuration(string packageId)
        => "settings-schema";
}
