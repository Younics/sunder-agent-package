using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Mcp.Runtime;

public interface IMcpWorkspaceCatalog : IAsyncDisposable
{
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    Task<IReadOnlyList<AgentWorkspaceRecord>> ListKnownWorkspacesAsync(CancellationToken cancellationToken = default);
}
