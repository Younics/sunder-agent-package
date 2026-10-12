extern alias SkillsWorker;
extern alias SubagentsWorker;
extern alias McpWorker;

using Sunder.Sdk.Services;
using SkillsServices = SkillsWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using McpServices = McpWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using SubagentServices = SubagentsWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using SubagentDomain = SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using SubagentRuntime = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Tests;

internal static partial class RegressionTestFamilyConsumers
{
    internal static IServiceProviders<McpServices.IRuntimeCatalogService> McpWorkspaces(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("sunder.package.agent.mcp").Providers<McpServices.IRuntimeCatalogService>();

    internal static GeneratedServiceTestScope Subagents(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("sunder.package.agent.subagents");

    internal static IServiceProviders<SkillsServices.IRuntimeCatalogService> Skills(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("sunder.package.agent.skills").Providers<SkillsServices.IRuntimeCatalogService>();

    internal static SubagentDomain.SubagentFeature CreateSubagentFeature(
        this RegressionTestExtensionCatalog catalog, SubagentDomain.SubagentService service)
    {
        var receiver = catalog.Subagents();
        var runtimes = receiver.Providers<SubagentServices.IRuntimeCatalogService>();
        var permission = new SubagentDomain.SubagentPermissionStatusAdapter(receiver.Providers<SubagentServices.IToolSourceService>());
        var parser = new SubagentDomain.SubagentRequestParser();
        var renderer = new SubagentDomain.SubagentBatchResultRenderer(service, parser, permission);
        var descriptors = new SubagentDomain.SubagentDescriptorSchema(service, permission, runtimes,
            receiver.Providers<SubagentServices.IBehaviorLoopService>());
        var children = new SubagentDomain.SubagentChildRunCoordinator(runtimes,
            receiver.Providers<SubagentServices.IChildRunExecutorService>(), descriptors, permission, renderer);
        var orchestrator = new SubagentDomain.SubagentDelegationOrchestrator(parser, descriptors, children, permission, renderer);
        return new(service, descriptors, orchestrator, renderer);
    }

    internal static async ValueTask<RegressionSubagentManagement> CreateSubagentRuntimeManagementAsync(
        this RegressionTestExtensionCatalog catalog, SubagentDomain.SubagentService service,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var receiver = catalog.Subagents();
        var runtimes = receiver.Providers<SubagentServices.IRuntimeCatalogService>();
        var chats = receiver.Providers<SubagentServices.IChatProviderService>();
        var capabilities = new SubagentDomain.SubagentEditorCapabilityCatalog(
            receiver.Providers<SubagentServices.IToolSourceService>(),
            receiver.Providers<SubagentServices.ISelectableCapabilityProviderService>(), chats, runtimes);
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
