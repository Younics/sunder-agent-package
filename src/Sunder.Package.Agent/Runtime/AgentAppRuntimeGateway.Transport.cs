using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    private bool IsRuntimeAvailabilityFailure(Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        if (exception is PackageRuntimeInvocationException invocationException)
        {
            return invocationException.IsTransient;
        }
        if (!_transport.IsAvailable)
        {
            return true;
        }

        return exception is HttpRequestException
               or System.Net.Sockets.SocketException
               or TimeoutException
               or OperationCanceledException;
    }

    private void ThrowIfUnavailable()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AgentAppRuntimeGateway));
        if (!_transport.IsAvailable)
        {
            SetConnectionState(AgentRuntimeConnectionState.Unavailable);
            throw new InvalidOperationException("Agent Runtime is unavailable. Reconnect Runtime and try again.");
        }
    }
}

internal sealed class AgentRuntimeTransport(IPackageRuntimeClient client)
{
    private readonly IPackageRuntimeClient _client = client;

    public bool IsAvailable => _client.IsAvailable;

    public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TResponse : class
        => _client.InvokeAsync(operation, request, cancellationToken);

    public IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        TRequest request,
        CancellationToken cancellationToken)
        where TRequest : class
        where TEvent : class
        => _client.SubscribeAsync(stream, request, cancellationToken);
}
