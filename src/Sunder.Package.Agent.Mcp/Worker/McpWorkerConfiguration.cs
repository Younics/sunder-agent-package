using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Mcp.Worker;

internal static class McpWorkerConfiguration
{
    internal static SunderWorkerOptions Create(SunderWorkerContext context)
        => CreateCore(context, context.Rpc);

    internal static SunderWorkerOptions CreateCore(
        IPackageContext context,
        ISunderRpcClient rpc)
    {
        var rpcCatalog = AgentRpcCatalog.CreateDormant(rpc);
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton(rpcCatalog);
        services.AddSingleton<McpServerCatalogService>();
        services.AddSingleton<McpOAuthService>();
        services.AddSingleton<McpOAuthCallbackHandler>();
        services.AddSingleton(provider => new McpClientConnectionManager(
            context.Logging.LoggerFactory,
            provider.GetRequiredService<McpOAuthService>()));
        services.AddSingleton<McpEcosystemConfigurationImporter>();
        services.AddSingleton(provider => new McpSunderConfigurationSyncService(
            provider.GetRequiredService<McpEcosystemConfigurationImporter>(),
            rpcCatalog));
        services.AddSingleton<McpConfigurationCoordinator>();
        services.AddSingleton<McpSettingsEditorService>();
        services.AddSingleton<McpServerConnectionService>();
        services.AddSingleton<McpOAuthCoordinator>();
        services.AddSingleton<McpToolSource>();
        services.AddSingleton<McpSessionConnectionCleaner>();
        services.AddSingleton<McpServerStackContributor>();
        services.AddSingleton<McpRuntimeHandler>();
        services.AddSingleton<McpRuntimeChangeStream>();
        services.AddSingleton<McpManagementRpcProvider>();

        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var toolSource = serviceProvider.GetRequiredService<McpToolSource>();
        var management = serviceProvider.GetRequiredService<McpManagementRpcProvider>();
        var descriptor = McpManagementProtocol.Descriptor;

        return new SunderWorkerOptions(
        [
            Register(
                "mcp.tools",
                AgentRpcContractIds.ToolSource,
                AgentToolSourceRpc.CreateHandler(toolSource)),
            Register(
                "mcp.selectable.capabilities",
                AgentRpcContractIds.SelectableCapabilityProvider,
                AgentSelectableCapabilityProviderRpc.CreateHandler(toolSource)),
            Register(
                "mcp.session.cleaner",
                AgentRpcContractIds.SessionCleaner,
                AgentSessionCleanerRpc.CreateHandler(serviceProvider.GetRequiredService<McpSessionConnectionCleaner>())),
            SunderStackContributorRpc.CreateWorkerProviderRegistration(
                "mcp.stack",
                serviceProvider.GetRequiredService<McpServerStackContributor>()),
            new SunderWorkerProviderRegistration(
                McpManagementProtocol.ProviderId,
                descriptor.ContractId,
                descriptor.Version,
                descriptor.Sha256,
                descriptor.AbiSha256 ?? throw new InvalidDataException(
                    "The MCP management RPC descriptor is missing its V2 ABI identity."),
                new Protocol.Generated.SunderPackageAgentMcpManagementRpcHandler(management)),
        ])
        {
            Composition = new SunderWorkerComposition(serviceProvider),
            CallbackHandlers =
            [
                serviceProvider.GetRequiredService<McpOAuthCallbackHandler>(),
            ],
            OnActivated = (_, _) =>
            {
                serviceProvider.GetRequiredService<McpConfigurationCoordinator>().Start();
                rpcCatalog.StartWatching();
                return ValueTask.CompletedTask;
            },
        };
    }

    private static SunderWorkerProviderRegistration Register(
        string providerId,
        string contractId,
        ISunderRpcServiceHandler handler)
    {
        var descriptor = AgentRpcContractDescriptors.Get(contractId);
        return new SunderWorkerProviderRegistration(
            providerId,
            descriptor.ContractId,
            descriptor.Version,
            descriptor.Sha256,
            descriptor.AbiSha256 ?? throw new InvalidOperationException(
                $"Agent RPC contract '{descriptor.ContractId}' does not have a V2 ABI identity."),
            handler);
    }
}
