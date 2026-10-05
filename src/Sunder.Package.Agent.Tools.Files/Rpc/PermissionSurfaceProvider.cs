using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Imports.CSunder_Agent_Permission_Surface;

namespace Sunder.Package.Agent.Tools.Files.Rpc;

[SunderRpcProvider(typeof(IPermissionsService), ProviderId = "files.permissions")]
internal sealed partial class PermissionSurfaceProvider(FilesToolSource source) : IPermissionsService
{
    public ValueTask<Wire.PermissionSurfaceSnapshot> DescribeAsync(SunderRpcInvocationContext context, Wire.Unit request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new Wire.PermissionSurfaceSnapshot
        { SurfaceId = source.SurfaceId, DisplayName = source.DisplayName, Actions = FilesProviderValues.Convert<Wire.AgentPermissionActionDescriptor[]>(source.ListActions()) });
    }
}
