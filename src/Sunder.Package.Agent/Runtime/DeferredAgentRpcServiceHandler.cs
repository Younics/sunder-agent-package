using System.Collections.ObjectModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Runtime;

internal sealed class DeferredAgentRpcServiceHandler : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
{
    private readonly string _providerId;
    private readonly string _contractId;
    private readonly Func<IServiceProvider, ISunderRpcServiceHandler> _createHandler;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _retirement = new();
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> _declaredErrors;
    private ISunderRpcServiceHandler? _handler;
    private readonly TaskCompletionSource<ISunderRpcServiceHandler> _activated =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _retired;

    internal DeferredAgentRpcServiceHandler(
        Func<ISunderRpcServiceHandler, SunderWorkerProviderRegistration> declare,
        Func<IServiceProvider, SunderWorkerProviderRegistration> activate)
    {
        ArgumentNullException.ThrowIfNull(declare);
        ArgumentNullException.ThrowIfNull(activate);
        Registration = declare(this) ?? throw new InvalidOperationException("The generated provider declaration returned no registration.");
        _providerId = Registration.ProviderId;
        _contractId = Registration.ContractId;
        _declaredErrors = Registration.DeclaredErrors;
        _createHandler = services =>
        {
            var actual = activate(services) ?? throw new InvalidOperationException($"Agent RPC provider '{_providerId}' returned no registration.");
            if (actual.ProviderId != Registration.ProviderId || actual.ContractId != Registration.ContractId
                || actual.ContractVersion != Registration.ContractVersion || actual.DescriptorSha256 != Registration.DescriptorSha256
                || actual.AbiSha256 != Registration.AbiSha256 || !DeclaredErrorsMatch(_declaredErrors, actual.DeclaredErrors))
                throw new InvalidOperationException($"Agent RPC provider '{_providerId}' activation does not match its generated declaration.");
            return actual.Handler;
        };
    }

    // Retained for the descriptor-first composition until the canonical source switch.
    internal DeferredAgentRpcServiceHandler(string providerId, SunderRpcContractDescriptor descriptor,
        Func<IServiceProvider, ISunderRpcServiceHandler> createHandler)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(createHandler);
        _providerId = providerId;
        _contractId = descriptor.ContractId;
        _createHandler = createHandler;
        _declaredErrors = new ReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(descriptor.Services.ToDictionary(
            service => service.ServiceId,
            service => (IReadOnlyDictionary<string, IReadOnlyList<string>>)new ReadOnlyDictionary<string, IReadOnlyList<string>>(
                service.Methods.ToDictionary(method => method.MethodId, method => (IReadOnlyList<string>)Array.AsReadOnly(method.Errors.ToArray()), StringComparer.Ordinal)),
            StringComparer.Ordinal));
        Registration = new(providerId, descriptor.ContractId, descriptor.Version, descriptor.Sha256, descriptor.AbiSha256!, this);
    }

    internal SunderWorkerProviderRegistration Registration { get; }

    internal bool IsActive => Volatile.Read(ref _retired) == 0
                               && Volatile.Read(ref _handler) is not null;

    public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors
        => _declaredErrors;

    internal void Activate(IServiceProvider services)
    {
        lock (_gate)
        {
            if (_retired != 0)
            {
                throw new InvalidOperationException($"Agent RPC provider '{_providerId}' is retired.");
            }
            if (_handler is not null)
            {
                throw new InvalidOperationException($"Agent RPC provider '{_providerId}' was activated more than once.");
            }

            var handler = _createHandler(services)
                ?? throw new InvalidOperationException($"Agent RPC provider '{_providerId}' returned no handler.");
            if (handler is not ISunderRpcDeclaredErrorMetadata metadata
                || !DeclaredErrorsMatch(_declaredErrors, metadata.DeclaredErrors))
            {
                throw new InvalidOperationException(
                    $"Agent RPC provider '{_providerId}' does not implement the declared domain errors for contract '{_contractId}'.");
            }
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

    private static bool DeclaredErrorsMatch(
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> expected,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> actual)
    {
        if (expected.Count != actual.Count)
        {
            return false;
        }
        foreach (var (serviceId, expectedMethods) in expected)
        {
            if (!actual.TryGetValue(serviceId, out var actualMethods)
                || expectedMethods.Count != actualMethods.Count)
            {
                return false;
            }
            foreach (var (methodId, expectedErrors) in expectedMethods)
            {
                if (!actualMethods.TryGetValue(methodId, out var actualErrors)
                    || !expectedErrors.SequenceEqual(actualErrors, StringComparer.Ordinal))
                {
                    return false;
                }
            }
        }
        return true;
    }
}
