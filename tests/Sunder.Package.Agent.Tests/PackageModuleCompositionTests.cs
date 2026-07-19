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
                Extension("sunder.package.agent:runtime-catalogs", "Sunder.Package.Agent.Services.AgentRuntimeCatalog"),
                Extension("sunder.package.agent:workspace-execution-resolvers", "Sunder.Package.Agent.Services.AgentWorkspaceExecutionResolver"),
                Extension("sunder.package.agent:child-run-executors", "Sunder.Package.Agent.Services.AgentRunCoordinator"),
                Extension("sunder.package.agent:attachment-content-stores", "Sunder.Package.Agent.Services.AgentAttachmentService"),
                Extension("sunder.package.agent:session-data-cleaners", "Sunder.Package.Agent.Services.AgentAttachmentService"),
                Extension("sunder.package.agent:behavior-loops", "Sunder.Package.Agent.Services.BehaviorLoops.DefaultAgentBehaviorLoop"),
                Extension("sunder.package.agent:system-prompt-contributors", "Sunder.Package.Agent.Services.WorkspaceDocumentationContextService"),
                StackExporter("Sunder.Package.Agent.Services.AgentProfileStackContributor"),
                StackImporter("Sunder.Package.Agent.Services.AgentProfileStackContributor"),
                StackImportAppliedHandler("Sunder.Package.Agent.Services.AgentProfileStackContributor"),
                StackExporter("Sunder.Package.Agent.Services.AgentWorkspaceStackContributor"),
                StackImporter("Sunder.Package.Agent.Services.AgentWorkspaceStackContributor"),
                StackImportAppliedHandler("Sunder.Package.Agent.Services.AgentWorkspaceStackContributor"),
                RuntimeOperation("agent.chat.snapshot.v1", "Sunder.Package.Agent.Runtime.AgentChatSnapshotHandler"),
                RuntimeOperation("agent.dashboard.v1", "Sunder.Package.Agent.Runtime.AgentDashboardHandler"),
                RuntimeOperation("agent.transcript.page.v1", "Sunder.Package.Agent.Runtime.AgentTranscriptPageHandler"),
                RuntimeOperation("agent.catalog.v1", "Sunder.Package.Agent.Runtime.AgentCatalogHandler"),
                RuntimeOperation("agent.profiles.command.v1", "Sunder.Package.Agent.Runtime.AgentProfileCommandHandler"),
                RuntimeOperation("agent.workspaces.command.v1", "Sunder.Package.Agent.Runtime.AgentWorkspaceCommandHandler"),
                RuntimeOperation("agent.sessions.command.v1", "Sunder.Package.Agent.Runtime.AgentSessionCommandHandler"),
                RuntimeOperation("agent.runs.command.v1", "Sunder.Package.Agent.Runtime.AgentRunCommandHandler"),
                RuntimeOperation("agent.runs.status.v1", "Sunder.Package.Agent.Runtime.AgentRunCommandHandler"),
                RuntimeOperation("agent.permissions.command.v1", "Sunder.Package.Agent.Runtime.AgentPermissionCommandHandler"),
                RuntimeOperation("agent.attachments.read.v1", "Sunder.Package.Agent.Runtime.AgentAttachmentReadHandler"),
                RuntimeStream("agent.changes.v1", "Sunder.Package.Agent.Runtime.AgentRuntimeChangeHub"),
                PackageView("sunder.package.agent.chat", "Sunder.Package.Agent.PackageViews.AgentChatView"),
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
                Configuration("sunder.package.agent.execution.docker"),
                StackExporter("Sunder.Package.Agent.Execution.Docker.DockerImageStackContributor"),
                StackImporter("Sunder.Package.Agent.Execution.Docker.DockerImageStackContributor"),
                StackImportAppliedHandler("Sunder.Package.Agent.Execution.Docker.DockerImageStackContributor"),
                SettingsView("Sunder.Package.Agent.Execution.Docker.DockerExecutionSettingsView"),
                Extension("sunder.package.agent:execution-targets", "Sunder.Package.Agent.Execution.Docker.DockerExecutionTarget"),
                Extension("sunder.package.agent:workspace-binding-contributors", "Sunder.Package.Agent.Execution.Docker.DockerExecutionTarget"),
                Extension("sunder.package.agent:workspace-path-migration-contributors", "Sunder.Package.Agent.Execution.Docker.DockerExecutionWorkspaceConfigService"),
                Extension("sunder.package.agent:workspace-editor-contributors", "Sunder.Package.Agent.Execution.Docker.DockerExecutionWorkspaceEditorContributor"),
                Extension("sunder.package.agent:workspace-editor-contributors", "Sunder.Package.Agent.Execution.Docker.DockerExecutionWorkspaceEditorPresentationContributor"),
                RuntimeOperation("docker-execution.presentation.v1", "Sunder.Package.Agent.Execution.Docker.DockerExecutionRuntimeOperationHandler"),
            ],
            ["Sunder.Package.Agent.Execution.Local"] =
            [
                Configuration("sunder.package.agent.execution.local"),
                SettingsView("Sunder.Package.Agent.Execution.Local.LocalExecutionSettingsView"),
                Extension("sunder.package.agent:execution-targets", "Sunder.Package.Agent.Execution.Local.LocalExecutionTarget"),
                Extension("sunder.package.agent:workspace-binding-contributors", "Sunder.Package.Agent.Execution.Local.LocalExecutionTarget"),
                Extension("sunder.package.agent:workspace-path-migration-contributors", "Sunder.Package.Agent.Execution.Local.LocalExecutionWorkspaceConfigService"),
                Extension("sunder.package.agent:workspace-editor-contributors", "Sunder.Package.Agent.Execution.Local.LocalExecutionWorkspaceEditorContributor"),
                Extension("sunder.package.agent:workspace-editor-contributors", "Sunder.Package.Agent.Execution.Local.LocalExecutionWorkspaceEditorPresentationContributor"),
                RuntimeOperation("local-execution.presentation.v1", "Sunder.Package.Agent.Execution.Local.LocalExecutionRuntimeOperationHandler"),
            ],
            ["Sunder.Package.Agent.Mcp"] =
            [
                SettingsView("Sunder.Package.Agent.Mcp.AgentMcpSettingsView"),
                Extension("sunder.package.agent:tool-sources", "Sunder.Package.Agent.Mcp.McpToolSource"),
                Extension("sunder.package.agent:profile-selectable-capability-providers", "Sunder.Package.Agent.Mcp.McpToolSource"),
                StackExporter("Sunder.Package.Agent.Mcp.Services.McpServerStackContributor"),
                StackImporter("Sunder.Package.Agent.Mcp.Services.McpServerStackContributor"),
                StackImportAppliedHandler("Sunder.Package.Agent.Mcp.Services.McpServerStackContributor"),
                RuntimeOperation("mcp.query.v1", "Sunder.Package.Agent.Mcp.Runtime.McpRuntimeHandler"),
                RuntimeOperation("mcp.command.v1", "Sunder.Package.Agent.Mcp.Runtime.McpRuntimeHandler"),
                RuntimeStream("mcp.changes.v1", "Sunder.Package.Agent.Mcp.Runtime.McpRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Memory.Semantic"] =
            [
                Configuration("sunder.package.agent.memory.semantic"),
                BackgroundService("Sunder.Package.Agent.Memory.Semantic.Services.SemanticMemoryIndexingBackgroundService"),
                Extension("sunder.package.agent:prompt-context-contributors", "Sunder.Package.Agent.Memory.Semantic.MemorySemanticFeature"),
                Extension("sunder.package.agent:lifecycle-observers", "Sunder.Package.Agent.Memory.Semantic.MemorySemanticFeature"),
                Extension("sunder.package.agent:profile-capability-consumers", "Sunder.Package.Agent.Memory.Semantic.MemorySemanticFeature"),
                Extension("sunder.package.agent:session-data-cleaners", "Sunder.Package.Agent.Memory.Semantic.MemorySemanticFeature"),
                PackageView("sunder.package.agent.memory.semantic.inspector", "Sunder.Package.Agent.Memory.Semantic.PackageViews.MemoryInspectorView"),
                RuntimeOperation("semantic-memory.query.v1", "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryRuntimeHandler"),
                RuntimeOperation("semantic-memory.command.v1", "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryRuntimeHandler"),
                RuntimeStream("semantic-memory.changes.v1", "Sunder.Package.Agent.Memory.Semantic.Runtime.MemoryRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Provider.Anthropic"] =
            [
                Configuration("sunder.package.agent.provider.anthropic"),
                SettingsView("Sunder.Package.Agent.Provider.Anthropic.AnthropicSettingsView"),
                Extension("sunder.package.agent:chat-providers", "Sunder.Package.Agent.Provider.Anthropic.AnthropicAgentProvider"),
            ],
            ["Sunder.Package.Agent.Provider.Gemini"] =
            [
                Configuration("sunder.package.agent.provider.gemini"),
                SettingsView("Sunder.Package.Agent.Provider.Gemini.GeminiSettingsView"),
                Extension("sunder.package.agent:chat-providers", "Sunder.Package.Agent.Provider.Gemini.GeminiAgentProvider"),
                Extension("sunder.package.agent:embedding-providers", "Sunder.Package.Agent.Provider.Gemini.GeminiEmbeddingProvider"),
            ],
            ["Sunder.Package.Agent.Provider.LMStudio"] =
            [
                Configuration("sunder.package.agent.provider.lmstudio"),
                SettingsView("Sunder.Package.Agent.Provider.LMStudio.LMStudioSettingsView"),
                Extension("sunder.package.agent:chat-providers", "Sunder.Package.Agent.Provider.LMStudio.LMStudioAgentProvider"),
                Extension("sunder.package.agent:embedding-providers", "Sunder.Package.Agent.Provider.LMStudio.LMStudioEmbeddingProvider"),
            ],
            ["Sunder.Package.Agent.Provider.OpenAI"] =
            [
                Configuration("sunder.package.agent.provider.openai"),
                SettingsView("Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsView"),
                Extension("sunder.package.agent:chat-providers", "Sunder.Package.Agent.Provider.OpenAI.OpenAiAgentProvider"),
                Extension("sunder.package.agent:embedding-providers", "Sunder.Package.Agent.Provider.OpenAI.OpenAiEmbeddingProvider"),
                Extension("sunder.package.agent:session-data-cleaners", "Sunder.Package.Agent.Provider.OpenAI.Transport.CodexResponseContinuationStore"),
                RuntimeOperation("openai.auth.v1", "Sunder.Package.Agent.Provider.OpenAI.OpenAiAuthOperationHandler"),
            ],
            ["Sunder.Package.Agent.Skills"] =
            [
                SettingsView("Sunder.Package.Agent.Skills.PackageViews.SkillSettingsView"),
                Extension("sunder.package.agent:profile-selectable-capability-providers", "Sunder.Package.Agent.Skills.Services.SkillsFeature"),
                Extension("sunder.package.agent:tool-sources", "Sunder.Package.Agent.Skills.Services.SkillsFeature"),
                Extension("sunder.package.agent:system-prompt-contributors", "Sunder.Package.Agent.Skills.Services.SkillsFeature"),
                Extension("sunder.package.agent:execution-resource-providers", "Sunder.Package.Agent.Skills.Services.SkillsFeature"),
                StackExporter("Sunder.Package.Agent.Skills.Services.SkillStackContributor"),
                StackImporter("Sunder.Package.Agent.Skills.Services.SkillStackContributor"),
                StackImportAppliedHandler("Sunder.Package.Agent.Skills.Services.SkillStackContributor"),
                RuntimeOperation("skills.query.v1", "Sunder.Package.Agent.Skills.Runtime.SkillRuntimeHandler"),
                RuntimeOperation("skills.command.v1", "Sunder.Package.Agent.Skills.Runtime.SkillRuntimeHandler"),
                RuntimeStream("skills.changes.v1", "Sunder.Package.Agent.Skills.Runtime.SkillRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Subagents"] =
            [
                PackageView("sunder.package.agent.subagents.sessions", "Sunder.Package.Agent.Subagents.PackageViews.SubsessionsView"),
                PackageView("sunder.package.agent.subagents", "Sunder.Package.Agent.Subagents.PackageViews.SubagentsView"),
                Extension("sunder.package.agent:profile-selectable-capability-providers", "Sunder.Package.Agent.Subagents.Services.SubagentFeature"),
                Extension("sunder.package.agent:tool-sources", "Sunder.Package.Agent.Subagents.Services.SubagentFeature"),
                Extension("sunder.package.agent:system-prompt-contributors", "Sunder.Package.Agent.Subagents.Services.SubagentFeature"),
                Extension("sunder.package.agent:behavior-loops", "Sunder.Package.Agent.Subagents.Services.OrchestratedAgentBehaviorLoop"),
                StackExporter("Sunder.Package.Agent.Subagents.Services.SubagentStackContributor"),
                StackImporter("Sunder.Package.Agent.Subagents.Services.SubagentStackContributor"),
                StackImportAppliedHandler("Sunder.Package.Agent.Subagents.Services.SubagentStackContributor"),
                RuntimeOperation("subagents.query.v1", "Sunder.Package.Agent.Subagents.Runtime.SubagentRuntimeHandler"),
                RuntimeOperation("subagents.command.v1", "Sunder.Package.Agent.Subagents.Runtime.SubagentRuntimeHandler"),
                RuntimeStream("subagents.changes.v1", "Sunder.Package.Agent.Subagents.Runtime.SubagentRuntimeChangeStream"),
            ],
            ["Sunder.Package.Agent.Tools.Files"] =
            [
                Extension("sunder.package.agent:tool-sources", "Sunder.Package.Agent.Tools.Files.FilesToolSource"),
                Extension("sunder.package.agent:permission-surfaces", "Sunder.Package.Agent.Tools.Files.FilesToolSource"),
                Extension("sunder.package.agent:system-prompt-contributors", "Sunder.Package.Agent.Tools.Files.FilesToolSource"),
            ],
            ["Sunder.Package.Agent.Tools.Shell"] =
            [
                Extension("sunder.package.agent:tool-sources", "Sunder.Package.Agent.Tools.Shell.ShellToolSource"),
                Extension("sunder.package.agent:permission-surfaces", "Sunder.Package.Agent.Tools.Shell.ShellToolSource"),
            ],
            ["Sunder.Package.Agent.Tools.Web"] =
            [
                Configuration("sunder.package.agent.tools.web"),
                Extension("sunder.package.agent:tools", "Sunder.Package.Agent.Tools.Web.WebFetchTool"),
                Extension("sunder.package.agent:tools", "Sunder.Package.Agent.Tools.Web.WebSearchTool"),
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
        services.AddSingleton<IPackageExtensionCatalog>(extensionCatalog);
        services.AddSingleton<Sunder.Sdk.Runtime.IPackageRuntimeClient>(Sunder.Sdk.Runtime.NullPackageRuntimeClient.Instance);
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
        foreach (var contribution in registry.ActivatedContributions)
        {
            Assert.Same(contribution, serviceProvider.GetRequiredService(contribution.GetType()));
        }

        if (string.Equals(packageName, "Sunder.Package.Agent", StringComparison.Ordinal))
        {
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentRunPreparationService>());
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentRunStartService>());
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentRunExecutionService>());
            Assert.NotNull(serviceProvider.GetRequiredService<Services.AgentUserMessageRunCoordinator>());
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

    private static string Extension(string extensionPoint, string implementationType)
        => $"extension:{extensionPoint}:{implementationType}";

    private static string StackExporter(string implementationType)
        => Extension("sunder:stack-exporters", implementationType);

    private static string StackImporter(string implementationType)
        => Extension("sunder:stack-importers", implementationType);

    private static string StackImportAppliedHandler(string implementationType)
        => Extension("sunder:stack-import-applied-handlers", implementationType);

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
