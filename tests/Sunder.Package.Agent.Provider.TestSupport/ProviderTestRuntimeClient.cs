using System.Collections.Concurrent;
using System.Text.Json;
using Sunder.Sdk.Runtime;

namespace Sunder.Package.Agent.Provider.TestSupport;

public sealed record ProviderTestRuntimeInvocation(
    string OperationId,
    string RequestJson,
    string ResponseJson);

public sealed class ProviderTestRuntimeClient(
    Func<string, object, CancellationToken, ValueTask<object>> invoke) : IPackageRuntimeClient
{
    private readonly ConcurrentQueue<ProviderTestRuntimeInvocation> _invocations = new();

    public bool IsAvailable => true;

    public IReadOnlyList<ProviderTestRuntimeInvocation> Invocations => _invocations.ToArray();

    public async ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
        PackageRuntimeOperation<TRequest, TResponse> operation,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TResponse : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await invoke(operation.OperationId, request, cancellationToken);
        _invocations.Enqueue(new ProviderTestRuntimeInvocation(
            operation.OperationId,
            JsonSerializer.Serialize(request, request.GetType()),
            JsonSerializer.Serialize(response, response.GetType())));
        return (TResponse)response;
    }

    public IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
        PackageRuntimeStream<TRequest, TEvent> stream,
        TRequest request,
        CancellationToken cancellationToken = default)
        where TRequest : class
        where TEvent : class
        => throw new NotSupportedException();
}
