using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Mcp.Worker;

internal sealed class McpActivation(IMcpWorkspaceCatalog catalog, McpConfigurationCoordinator configuration) : ISunderWorkerActivationService
{
    public async ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
    {
        await catalog.StartAsync(cancellationToken).ConfigureAwait(false);
        configuration.Start();
    }
}
