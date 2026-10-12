namespace Sunder.Package.Agent.Runtime;

internal sealed partial class AgentAppRuntimeGateway
{
    public async Task<AgentDashboardProjection> LoadDashboardAsync(
        CancellationToken cancellationToken = default)
    {
        Task<AgentDashboardProjection> load;
        bool retryIfFaulted;
        lock (_cacheLock)
        {
            if (_dashboard is not null)
            {
                return _dashboard;
            }
            if (_dashboardLoad is { IsFaulted: true } or { IsCanceled: true })
            {
                _ = _dashboardLoad.Exception;
                _dashboardLoad = null;
                _abandonedDashboardLoad = null;
            }
            if (_dashboardLoad is null)
            {
                _dashboardLoad = StartRetainedProducerTask(
                    () => Task.Run(PopulateDashboardLoadAsync));
            }
            load = _dashboardLoad;
            retryIfFaulted = ReferenceEquals(_abandonedDashboardLoad, load);
        }

        try
        {
            var dashboard = await load.WaitAsync(cancellationToken).ConfigureAwait(false);
            var acceptedLoad = false;
            lock (_cacheLock)
            {
                if (ReferenceEquals(_dashboardLoad, load))
                {
                    _dashboard = dashboard;
                    _dashboardLoad = null;
                    _abandonedDashboardLoad = null;
                    acceptedLoad = true;
                }
                else if (_dashboard is not null)
                {
                    return _dashboard;
                }
            }
            if (acceptedLoad)
            {
                AdvanceRevisionForDashboardWithoutActiveChat(dashboard.Revision);
                return dashboard;
            }
            cancellationToken.ThrowIfCancellationRequested();
            return await LoadDashboardAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            lock (_cacheLock)
            {
                if (ReferenceEquals(_dashboardLoad, load))
                {
                    _abandonedDashboardLoad = load;
                }
            }
            throw;
        }
        catch
        {
            AgentDashboardProjection? replacement = null;
            var retryDetachedLoad = false;
            lock (_cacheLock)
            {
                if (ReferenceEquals(_dashboardLoad, load))
                {
                    _dashboardLoad = null;
                }
                else if (_dashboard is not null)
                {
                    replacement = _dashboard;
                }
                else
                {
                    retryDetachedLoad = true;
                }
                if (ReferenceEquals(_abandonedDashboardLoad, load))
                {
                    _abandonedDashboardLoad = null;
                }
            }
            if (replacement is not null)
            {
                return replacement;
            }
            if ((retryIfFaulted || retryDetachedLoad)
                && !_lifetime.IsCancellationRequested)
            {
                return await LoadDashboardAsync(cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }

    private async Task<AgentDashboardProjection> PopulateDashboardLoadAsync()
        => await InvokeAsync(
                _owner.GetDashboardAsync,
                new AgentDashboardRequest(),
                _lifetime.Token)
            .ConfigureAwait(false);

    private void AdvanceRevisionForDashboardWithoutActiveChat(long revision)
    {
        lock (_observationLock)
        {
            if (_activeChatSnapshotRequest is null && !_chatSnapshotLoadPending)
            {
                _revision = Math.Max(_revision, revision);
            }
        }
    }

    public Task<AgentCatalogProjection> LoadCatalogAsync(
        AgentCatalogRequest request,
        CancellationToken cancellationToken = default)
    {
        int generation;
        lock (_cacheLock)
        {
            generation = _catalogGeneration;
        }
        var key = $"{generation}|{request.Profile?.ProfileId}|{request.Profile?.UpdatedAtUtc.UtcTicks}|{request.ChatProviderId}|{request.EmbeddingProviderId}";
        var lazy = _catalogs.GetOrAdd(key, _ => new Lazy<Task<AgentCatalogProjection>>(
            () => StartRetainedProducerTask(
                () => Task.Run(() =>
                    InvokeAsync(_owner.GetCatalogAsync, request, _lifetime.Token).AsTask())),
            LazyThreadSafetyMode.ExecutionAndPublication));
        if (lazy.IsValueCreated
            && lazy.Value is { IsFaulted: true } or { IsCanceled: true }
            && _catalogs.TryRemove(new KeyValuePair<string, Lazy<Task<AgentCatalogProjection>>>(key, lazy)))
        {
            _ = lazy.Value.Exception;
            return LoadCatalogAsync(request, cancellationToken);
        }
        return AwaitCatalogAsync(key, lazy, request, generation, cancellationToken);
    }

    private async Task<AgentCatalogProjection> AwaitCatalogAsync(
        string key,
        Lazy<Task<AgentCatalogProjection>> lazy,
        AgentCatalogRequest request,
        int generation,
        CancellationToken cancellationToken)
    {
        try
        {
            var projection = await lazy.Value.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_cacheLock)
            {
                if (generation == _catalogGeneration)
                {
                    _catalogSnapshot = projection;
                    return projection;
                }
            }
            _catalogs.TryRemove(new KeyValuePair<string, Lazy<Task<AgentCatalogProjection>>>(key, lazy));
            cancellationToken.ThrowIfCancellationRequested();
            return await LoadCatalogAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            _catalogs.TryRemove(new KeyValuePair<string, Lazy<Task<AgentCatalogProjection>>>(key, lazy));
            bool retryStaleGeneration;
            lock (_cacheLock)
            {
                retryStaleGeneration = generation != _catalogGeneration;
            }
            if (retryStaleGeneration && !_lifetime.IsCancellationRequested)
            {
                return await LoadCatalogAsync(request, cancellationToken).ConfigureAwait(false);
            }
            throw;
        }
    }
}
