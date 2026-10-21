extern alias SkillsWorker;
extern alias SubagentsWorker;
extern alias McpWorker;

using Sunder.Sdk.Services;
using SkillsRuntimeCatalogService = SkillsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using McpRuntimeCatalogService = McpWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using IBehaviorLoopService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Behavior_Loop.IBehaviorLoopService;
using IChatProviderService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using IChildRunExecutorService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Child_Run_Executor.IChildRunExecutorService;
using IRuntimeCatalogService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using ISelectableCapabilityProviderService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Selectable_Capability_Provider.ISelectableCapabilityProviderService;
using IToolSourceService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using SubagentDomain = SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using SubagentRuntime = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Tests;

internal static partial class RegressionTestFamilyConsumers
{
    internal static IServiceProviders<McpRuntimeCatalogService> McpWorkspaces(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("younics.ai.agent.mcp").Providers<McpRuntimeCatalogService>();

    internal static GeneratedServiceTestScope Subagents(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("younics.ai.agent.subagents");

    internal static IServiceProviders<SkillsRuntimeCatalogService> Skills(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("younics.ai.agent.skills").Providers<SkillsRuntimeCatalogService>();

    internal static SubagentDomain.SubagentFeature CreateSubagentFeature(
        this RegressionTestExtensionCatalog catalog, SubagentDomain.SubagentService service)
    {
        var receiver = catalog.Subagents();
        var runtimes = receiver.Providers<IRuntimeCatalogService>();
        var permission = new SubagentDomain.SubagentPermissionStatusAdapter(receiver.Providers<IToolSourceService>());
        var parser = new SubagentDomain.SubagentRequestParser();
        var renderer = new SubagentDomain.SubagentBatchResultRenderer(service, parser, permission);
        var descriptors = new SubagentDomain.SubagentDescriptorSchema(service, permission, runtimes,
            receiver.Providers<IBehaviorLoopService>());
        var children = new SubagentDomain.SubagentChildRunCoordinator(runtimes,
            receiver.Providers<IChildRunExecutorService>(), descriptors, permission, renderer);
        var orchestrator = new SubagentDomain.SubagentDelegationOrchestrator(parser, descriptors, children, permission, renderer);
        return new(service, descriptors, orchestrator, renderer);
    }

    internal static async ValueTask<RegressionSubagentManagement> CreateSubagentRuntimeManagementAsync(
        this RegressionTestExtensionCatalog catalog, SubagentDomain.SubagentService service,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receiver = catalog.Subagents();
        var runtimes = receiver.Providers<IRuntimeCatalogService>();
        var chats = receiver.Providers<IChatProviderService>();
        var capabilities = new SubagentDomain.SubagentEditorCapabilityCatalog(
            receiver.Providers<IToolSourceService>(),
            receiver.Providers<ISelectableCapabilityProviderService>(), chats, runtimes);
        var changes = new SubagentRuntime.SubagentRuntimeChangeStream(service, capabilities, runtimes);
        var fixture = new RegressionSubagentManagement(
            new SubagentRuntime.SubagentManagementService(service, capabilities, chats, runtimes, changes), capabilities, changes);
        try
        {
            await capabilities.StartAsync(cancellationToken).ConfigureAwait(false);
            await changes.StartAsync(cancellationToken).ConfigureAwait(false);
            return fixture;
        }
        catch
        {
            await fixture.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}

// Owns only the concrete package's business subscriptions. Its generated service
// catalogs remain borrowed from the caller-specific receiver owned by the fixture.
internal sealed class RegressionSubagentManagement(
    SubagentRuntime.SubagentManagementService service,
    SubagentDomain.SubagentEditorCapabilityCatalog capabilities,
    SubagentRuntime.SubagentRuntimeChangeStream changes) : IAsyncDisposable
{
    internal SubagentRuntime.SubagentManagementService Service { get; } = service;
    internal SubagentDomain.SubagentEditorCapabilityCatalog Capabilities { get; } = capabilities;
    internal SubagentRuntime.SubagentRuntimeChangeStream Changes { get; } = changes;

    public async ValueTask DisposeAsync()
    {
        try { await Changes.DisposeAsync().ConfigureAwait(false); }
        finally { await Capabilities.DisposeAsync().ConfigureAwait(false); }
    }
}
