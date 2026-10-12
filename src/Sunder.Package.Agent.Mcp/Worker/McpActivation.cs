using Sunder.Package.Agent.Mcp.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Mcp.Worker;

internal sealed class McpActivation(McpConfigurationCoordinator configuration) : ISunderWorkerActivationService
{
    public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        configuration.Start();
        return ValueTask.CompletedTask;
    }
}
