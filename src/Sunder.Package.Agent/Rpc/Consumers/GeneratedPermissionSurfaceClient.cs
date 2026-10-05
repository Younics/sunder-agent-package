using Sunder.Generated.Rpc;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Api = Sunder.Generated.Rpc.PackageApis.CSunder_Agent_Permission_Surface;
using Wire = Sunder.Generated.Rpc.PermissionSurface;

namespace Sunder.Package.Agent.Runtime.Rpc;

internal sealed class GeneratedPermissionSurfaceClient(ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    : IAgentPermissionSurface, IDisposable, IAsyncDisposable
{
    private readonly GeneratedProviderCallLifetime _calls = new(rpc, provider, "agent.permission.provider-stale");
    private Wire.PermissionSurfaceSnapshot? _snapshot;
    private Wire.PermissionSurfaceSnapshot Snapshot
    {
        get
        {
            _calls.ThrowIfStopped();
            return _snapshot ??= _calls.Invoke((scope, token) => new PackageApi(scope).PermissionSurface
                .CreateClient(scope, new Api.Provider(provider)).SPermission_2dsurface.DescribeAsync(new(), token),
                default).AsTask().GetAwaiter().GetResult();
        }
    }

    public string SurfaceId => Snapshot.SurfaceId;
    public string DisplayName => Snapshot.DisplayName;
    public IReadOnlyList<AgentPermissionActionDescriptor> ListActions()
        => AgentConsumerValues.Convert<AgentPermissionActionDescriptor[]>(Snapshot.Actions);

    public void Dispose() => _calls.Dispose();
    public ValueTask DisposeAsync() => _calls.DisposeAsync();
}
