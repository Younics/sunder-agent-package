using System.Runtime.CompilerServices;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Package.Agent.OwnerProtocol.Generated;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    private static bool IsRuntimeAvailabilityFailure(
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        if (exception is SunderRpcException rpcException)
        {
            return rpcException.Error.Kind is SunderRpcErrorKind.Unavailable
                or SunderRpcErrorKind.StaleEndpoint
                or SunderRpcErrorKind.NotFound;
        }

        return exception is HttpRequestException
               or System.Net.Sockets.SocketException
               or TimeoutException
               or OperationCanceledException;
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AgentAppRuntimeGateway));
    }
}

internal sealed class AgentOwnerRpcClient(ISunderRpcClient client) : IAgentOwnerClient
{
    private readonly ISunderRpcClient _client = client;

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        AgentOwnerRpcOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = await _client.CreateCallScopeAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var generatedClient = new Wire.AgentRuntimeClient(scope, endpoint);
        return await operation.InvokeAsync(generatedClient, request, cancellationToken).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        AgentOwnerRpcStream<TRequest, TEvent> stream,
        TRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : class
        where TEvent : class
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = await _client.CreateCallScopeAsync(
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var generatedClient = new Wire.AgentRuntimeClient(scope, endpoint);
        await foreach (var item in stream.SubscribeAsync(generatedClient, request, cancellationToken)
                           .WithCancellation(cancellationToken)
                           .ConfigureAwait(false))
        {
            yield return item;
        }
    }

    private async ValueTask<SunderRpcEndpointReference> ResolveEndpointAsync(
        CancellationToken cancellationToken)
    {
        var descriptor = AgentOwnerRuntimeRpc.Descriptor;
        var catalog = await _client.DiscoverAsync(
            descriptor.ContractId,
            cancellationToken).ConfigureAwait(false);
        var provider = catalog.Providers.SingleOrDefault(candidate =>
            candidate.State == SunderRpcProviderState.Active
            && string.Equals(candidate.ProviderId, AgentOwnerRuntimeRpc.ProviderId, StringComparison.Ordinal)
            && string.Equals(candidate.ContractVersion, descriptor.Version, StringComparison.Ordinal)
            && string.Equals(candidate.DescriptorSha256, descriptor.Sha256, StringComparison.Ordinal)
            && string.Equals(candidate.AbiSha256, descriptor.AbiSha256, StringComparison.Ordinal));
        return provider?.Endpoint
               ?? throw new SunderRpcException(new SunderRpcError(
                   SunderRpcErrorKind.Unavailable,
                   "agent.runtime.unavailable",
                   "The Agent Runtime owner provider is unavailable."));
    }
}
