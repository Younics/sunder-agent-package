using Sunder.Package.Agent.Builder.Protocol;
using Sunder.Package.Agent.Builder.Protocol.Generated;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

internal sealed class BuilderAppRpcGateway(ISunderRpcClient rpcClient) : IBuilderRpcGateway
{
    private const string PackageId = "sunder.package.agent.builder";
    private const string ProviderId = "builder.runtime";

    public async ValueTask<BuilderRpcResponse> InvokeAsync(
        BuilderRpcRequest request,
        CancellationToken cancellationToken = default)
    {
        if (BuilderRpcPayloadLimits.GetSerializedSize(request)
            >= BuilderRpcPayloadLimits.MaximumRequestBytes)
        {
            throw new InvalidOperationException("Package Builder RPC request exceeds the host transport limit.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var scope = await rpcClient
                .CreateCallScopeAsync(cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            var catalog = await scope
                .DiscoverAsync(BuilderRpcProtocol.Descriptor.ContractId, cancellationToken)
                .ConfigureAwait(false);
            var provider = catalog.Providers.FirstOrDefault(candidate =>
                candidate.PackageId == PackageId
                && candidate.ProviderId == ProviderId
                && candidate.State == SunderRpcProviderState.Active)
                ?? throw new SunderRpcException(new SunderRpcError(
                    SunderRpcErrorKind.Unavailable,
                    "agent.builder.workspace-execution-unavailable",
                    "Package Builder workspace execution is unavailable."));
            var response = await new BuilderRuntimeClient(scope, provider.Endpoint)
                .ExecuteAsync(BuilderRpcWireMapper.ToWire(request), cancellationToken)
                .ConfigureAwait(false);
            return BuilderRpcWireMapper.ToDomain(response);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw new OperationCanceledException(
                exception.Message,
                exception,
                cancellationToken.IsCancellationRequested ? cancellationToken : CancellationToken.None);
        }
    }
}
