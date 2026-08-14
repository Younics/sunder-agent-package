using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Runtime;

internal sealed class DeferredAgentRpcServiceHandler(
    string providerId,
    Func<IServiceProvider, ISunderRpcServiceHandler> createHandler) : ISunderRpcServiceHandler
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _retirement = new();
    private ISunderRpcServiceHandler? _handler;
    private readonly TaskCompletionSource<ISunderRpcServiceHandler> _activated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _retired;

    internal bool IsActive => Volatile.Read(ref _retired) == 0
                              && Volatile.Read(ref _handler) is not null;

    internal void Activate(IServiceProvider services)
    {
        lock (_gate)
        {
            if (_retired != 0)
            {
                throw new InvalidOperationException($"Agent RPC provider '{providerId}' is retired.");
            }
            if (_handler is not null)
            {
                throw new InvalidOperationException($"Agent RPC provider '{providerId}' was activated more than once.");
            }

            var handler = createHandler(services)
                ?? throw new InvalidOperationException($"Agent RPC provider '{providerId}' returned no handler.");
            Volatile.Write(ref _handler, handler);
            _activated.TrySetResult(handler);
        }
    }

    internal void Deactivate()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _retired, 1) != 0) return;
            Volatile.Write(ref _handler, null);
        }

        _ = ObserveRetirementAsync(_retirement.CancelAsync());
        _activated.TrySetCanceled(_retirement.Token);
    }

    public async ValueTask<JsonElement> InvokeUnaryAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        CancellationToken cancellationToken = default)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _retirement.Token);
        var handler = await GetHandlerAsync(invocation.Token).ConfigureAwait(false);
        invocation.Token.ThrowIfCancellationRequested();
        var response = await handler.InvokeUnaryAsync(
            context,
            serviceId,
            methodId,
            request,
            invocation.Token).ConfigureAwait(false);
        invocation.Token.ThrowIfCancellationRequested();
        return response;
    }

    public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
        SunderRpcInvocationContext context,
        string serviceId,
        string methodId,
        JsonElement request,
        [EnumeratorCancellation]
        CancellationToken cancellationToken = default)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _retirement.Token);
        var handler = await GetHandlerAsync(invocation.Token).ConfigureAwait(false);
        invocation.Token.ThrowIfCancellationRequested();
        await foreach (var item in handler.InvokeServerStreamAsync(
                           context,
                           serviceId,
                           methodId,
                           request,
                           invocation.Token).WithCancellation(invocation.Token).ConfigureAwait(false))
        {
            invocation.Token.ThrowIfCancellationRequested();
            yield return item;
        }
        invocation.Token.ThrowIfCancellationRequested();
    }

    private async Task<ISunderRpcServiceHandler> GetHandlerAsync(CancellationToken cancellationToken)
    {
        ThrowIfRetired();
        var handler = Volatile.Read(ref _handler)
                      ?? await _activated.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        ThrowIfRetired();
        if (!ReferenceEquals(handler, Volatile.Read(ref _handler)))
        {
            throw new OperationCanceledException(_retirement.Token);
        }
        return handler;
    }

    private void ThrowIfRetired()
    {
        if (Volatile.Read(ref _retired) != 0)
        {
            throw new OperationCanceledException(_retirement.Token);
        }
    }

    private static async Task ObserveRetirementAsync(Task callbacks)
    {
        try
        {
            await callbacks.ConfigureAwait(false);
        }
        catch
        {
            // Retirement is already visible; provider cancellation callbacks cannot roll it back.
        }
    }
}
