using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Protocol;

public partial class AgentRpcCatalog
{
    public async ValueTask<IReadOnlyList<AgentRpcReference<TClient>>> GetServiceReferencesAsync<TClient>(
        AgentRpcService<TClient> service,
        CancellationToken cancellationToken = default)
        where TClient : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var snapshot = await DiscoverAsync(service.ContractId, cancellationToken).ConfigureAwait(false);
        if (snapshot is null) return [];
        var active = snapshot.Providers
            .Where(static provider => provider.State == SunderRpcProviderState.Active)
            .ToArray();
        lock (_gate)
        {
            if (!_references.TryGetValue(service.ContractId, out var known))
            {
                known = new Dictionary<string, IRetirableAgentRpcReference>(StringComparer.Ordinal);
                _references.Add(service.ContractId, known);
            }
            var endpoints = active.Select(static provider => provider.Endpoint.Value).ToHashSet(StringComparer.Ordinal);
            foreach (var removed in known.Where(pair => !endpoints.Contains(pair.Key)).ToArray())
            {
                removed.Value.Retire();
                known.Remove(removed.Key);
            }
            var result = new List<AgentRpcReference<TClient>>(active.Length);
            foreach (var provider in active)
            {
                if (!known.TryGetValue(provider.Endpoint.Value, out var value)
                    || value is not AgentRpcReference<TClient> reference
                    || !reference.CanReuse(provider))
                {
                    value?.Retire();
                    value = new AgentRpcReference<TClient>(_client, service, provider);
                    known[provider.Endpoint.Value] = value;
                }
                result.Add((AgentRpcReference<TClient>)value);
            }
            return result;
        }
    }

    internal async ValueTask<AgentRpcReference<TClient>?> TryGetServiceReferenceAsync<TClient>(
        AgentRpcService<TClient> service,
        AgentRpcProviderHandle handle,
        CancellationToken cancellationToken = default)
        where TClient : class
    {
        try
        {
            return await GetServiceReferenceAsync(service, handle, cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return null;
        }
    }

    private async ValueTask<AgentRpcReference<TClient>> GetServiceReferenceAsync<TClient>(
        AgentRpcService<TClient> service,
        AgentRpcProviderHandle handle,
        CancellationToken cancellationToken)
        where TClient : class
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        ArgumentNullException.ThrowIfNull(handle);
        if (!string.Equals(handle.ContractId, service.ContractId, StringComparison.Ordinal))
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.Validation,
                "agent.rpc.contract-mismatch",
                "The RPC provider handle does not match the requested contract."));
        }

        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetime.Token);
        var provider = await _client.GetProviderAsync(
                new SunderRpcEndpointReference(handle.EndpointReference),
                invocation.Token)
            .ConfigureAwait(false);
        if (provider is null || !handle.Matches(provider))
        {
            throw new SunderRpcException(new SunderRpcError(
                SunderRpcErrorKind.StaleEndpoint,
                "agent.rpc.provider-stale",
                "The referenced RPC provider activation is stale."));
        }
        lock (_gate)
        {
            if (!_references.TryGetValue(service.ContractId, out var known))
            {
                known = new Dictionary<string, IRetirableAgentRpcReference>(StringComparer.Ordinal);
                _references.Add(service.ContractId, known);
            }
            if (!known.TryGetValue(provider.Endpoint.Value, out var value)
                || value is not AgentRpcReference<TClient> reference
                || !reference.CanReuse(provider))
            {
                value?.Retire();
                value = new AgentRpcReference<TClient>(_client, service, provider);
                known[provider.Endpoint.Value] = value;
            }
            return (AgentRpcReference<TClient>)value;
        }
    }
}

public sealed partial class AgentRpcProviderReference<TClient>
    where TClient : class
{
    public async ValueTask<AgentRpcLease<TClient>?> TryAcquireAsync(
        CancellationToken cancellationToken = default)
    {
        if (_retirement.IsCancellationRequested)
        {
            return null;
        }

        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _retirement.Token);
        SunderRpcProviderSnapshot? current;
        try
        {
            current = await _client.GetProviderAsync(Provider.Endpoint, invocation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _retirement.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind == SunderRpcErrorKind.Cancelled
            && _retirement.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind == SunderRpcErrorKind.Cancelled
            && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            if (exception.Error.Kind == SunderRpcErrorKind.StaleEndpoint) Retire();
            return null;
        }

        if (current is null
            || current.State != SunderRpcProviderState.Active
            || !AgentRpcProviderHandle.From(Provider).Matches(current))
        {
            Retire();
            return null;
        }
        if (_retirement.IsCancellationRequested)
        {
            return null;
        }

        return new AgentRpcLease<TClient>(Provider.PackageId, _binding, _retirement.Token);
    }
}

public sealed partial class AgentRpcReference<TClient>
    where TClient : class
{
    public async ValueTask<AgentRpcLease<TClient>?> TryAcquireAsync(
        CancellationToken cancellationToken = default)
    {
        if (_retirement.IsCancellationRequested)
        {
            return null;
        }

        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _retirement.Token);
        SunderRpcProviderSnapshot? current;
        try
        {
            current = await _client.GetProviderAsync(Provider.Endpoint, invocation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            _retirement.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind == SunderRpcErrorKind.Cancelled
            && _retirement.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind == SunderRpcErrorKind.Cancelled
            && cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(exception.Message, exception, cancellationToken);
        }
        catch (SunderRpcException exception) when (
            exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            if (exception.Error.Kind == SunderRpcErrorKind.StaleEndpoint) Retire();
            return null;
        }

        if (current is null
            || current.State != SunderRpcProviderState.Active
            || !ToHandle().Matches(current))
        {
            Retire();
            return null;
        }
        if (_retirement.IsCancellationRequested)
        {
            return null;
        }

        return new AgentRpcLease<TClient>(Provider.PackageId, _clientBinding, _retirement.Token);
    }
}
