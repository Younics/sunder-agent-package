using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using IPermissionSurfaceService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Permission_Surface.IPermissionSurfaceService;
using Sunder.Sdk.Services;

namespace RegressionTestGeneratedProviders;

[SunderServiceProvider(typeof(IPermissionSurfaceService), ProviderId = "fixture.regression.permission")]
public sealed partial class PermissionProvider(Func<string, Type, object?[], CancellationToken, object> invoke) : IPermissionSurfaceService
{
    private ValueTask<T> Reply<T>(string operation, object?[] arguments, CancellationToken token) => (ValueTask<T>)invoke(operation, typeof(T), arguments, token);
    public ValueTask<AgentPermissionSurfaceDescriptor> DescribeAsync(CancellationToken token = default) => Reply<AgentPermissionSurfaceDescriptor>(nameof(DescribeAsync), [], token);
}
