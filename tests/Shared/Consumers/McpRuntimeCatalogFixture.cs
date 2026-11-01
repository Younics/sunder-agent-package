extern alias McpWorker;

using Sunder.Sdk.Services;
using McpRuntimeCatalogService = McpWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;

namespace Sunder.Package.Agent.Tests;

internal static class McpRuntimeCatalogFixture
{
    internal static IServiceProviders<McpRuntimeCatalogService> McpWorkspaces(this RegressionTestExtensionCatalog catalog)
        => catalog.Receiver("younics.ai.agent.mcp").Providers<McpRuntimeCatalogService>();
}
