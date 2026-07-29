using Sunder.Package.Agent.HistorySearch;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentHistorySearchViewModel
{
    private enum StatusRefreshKind
    {
        None,
        Search,
    }

    private Task<HistorySearchState> LoadPrimaryStateAsync(CancellationToken cancellationToken)
        => _gateway.LoadHistoryStateAsync(
            new HistorySearchStateRequest(
                IncludeAdvancedFilters: false,
                WorkspaceId: _currentWorkspaceId),
            cancellationToken);

    private async Task<HistorySearchState> LoadAdvancedStatePagesAsync(CancellationToken cancellationToken)
    {
        var loaded = await _gateway.LoadHistoryStateAsync(
            new HistorySearchStateRequest(IncludeAdvancedFilters: true),
            cancellationToken).ConfigureAwait(false);
        if (loaded.Continuation is null)
        {
            return loaded;
        }

        var sessions = loaded.Sessions.ToList();
        var seenSessionIds = sessions.Select(static option => option.Id).ToHashSet(StringComparer.Ordinal);
        var seenContinuations = new HashSet<string>(StringComparer.Ordinal);
        var continuation = loaded.Continuation;
        var pageCount = 1;
        while (continuation is not null
               && pageCount < HistorySearchLimits.MaximumAdvancedFilterPages
               && sessions.Count < HistorySearchLimits.MaximumAdvancedSessionOptions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seenContinuations.Add(continuation))
            {
                throw new InvalidDataException("History session pagination returned a repeated continuation.");
            }
            var page = await _gateway.LoadHistoryStateAsync(
                new HistorySearchStateRequest(
                    Continuation: continuation,
                    Limit: Math.Min(
                        HistorySearchLimits.MaximumStateOptions,
                        HistorySearchLimits.MaximumAdvancedSessionOptions - sessions.Count),
                    IncludeAdvancedFilters: true),
                cancellationToken).ConfigureAwait(false);
            foreach (var session in page.Sessions.Take(
                         HistorySearchLimits.MaximumAdvancedSessionOptions - sessions.Count))
            {
                if (seenSessionIds.Add(session.Id))
                {
                    sessions.Add(session);
                }
            }
            loaded = loaded with { Status = page.Status };
            continuation = page.Continuation;
            pageCount++;
        }
        return loaded with { Sessions = sessions, Continuation = null };
    }

    private async Task LoadAdvancedFiltersAsync()
    {
        var current = ReplacementCancellation.CreateLinked(_lifetimeToken);
        var previous = Interlocked.Exchange(ref _advancedStateCancellation, current);
        CancelSafely(previous);
        var generation = Interlocked.Increment(ref _advancedStateGeneration);
        try
        {
            await RunOnUiThreadAsync(() => IsAdvancedLoading = true).ConfigureAwait(false);
            var loaded = await LoadAdvancedStatePagesAsync(current.Token).ConfigureAwait(false);
            current.Token.ThrowIfCancellationRequested();
            await RunOnUiThreadAsync(() =>
            {
                if (IsAdvancedExpanded
                    && generation == Volatile.Read(ref _advancedStateGeneration))
                {
                    ApplyAdvancedState(loaded);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (IsAdvancedExpanded
                    && generation == Volatile.Read(ref _advancedStateGeneration))
                {
                    RuntimeNotice = "Advanced filters could not be loaded. Local search remains available.";
                    NotifyResultStateChanged();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (generation == Volatile.Read(ref _advancedStateGeneration))
                {
                    IsAdvancedLoading = false;
                }
            }).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _advancedStateCancellation, null, current);
            current.Dispose();
        }
    }

    private async Task ResnapshotCurrentContextAsync()
    {
        CancelAdvancedFilterLoad();
        await RunOnUiThreadAsync(() =>
        {
            IsAdvancedLoading = false;
            _advancedFiltersLoaded = false;
        }).ConfigureAwait(false);
        var current = ReplacementCancellation.CreateLinked(_lifetimeToken);
        var previous = Interlocked.Exchange(ref _stateCancellation, current);
        CancelSafely(previous);
        var generation = Interlocked.Increment(ref _stateGeneration);
        try
        {
            await RunOnUiThreadAsync(InvalidateOutstandingSearch).ConfigureAwait(false);
            _currentWorkspaceId = await _selectionState.GetSelectedWorkspaceIdAsync(current.Token)
                .ConfigureAwait(false);
            var loaded = await LoadPrimaryStateAsync(current.Token).ConfigureAwait(false);
            current.Token.ThrowIfCancellationRequested();
            await RunOnUiThreadAsync(() =>
            {
                if (generation == Volatile.Read(ref _stateGeneration))
                {
                    ApplyPrimaryState(loaded, establishStatusBaseline: true);
                }
            }).ConfigureAwait(false);
            if (generation == Volatile.Read(ref _stateGeneration))
            {
                Volatile.Write(ref _contextRefreshPending, 0);
                await SearchAsync(append: false, TimeSpan.Zero, current.Token).ConfigureAwait(false);
            }
            if (IsAdvancedExpanded && generation == Volatile.Read(ref _stateGeneration))
            {
                _ = LoadAdvancedFiltersAsync();
            }
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            await RunOnUiThreadAsync(() =>
            {
                RuntimeNotice = "History search could not refresh its current scope. Existing results are unchanged.";
                NotifyResultStateChanged();
            }).ConfigureAwait(false);
        }
        finally
        {
            if (generation == Volatile.Read(ref _stateGeneration))
            {
                Volatile.Write(ref _contextRefreshPending, 0);
            }
            Interlocked.CompareExchange(ref _stateCancellation, null, current);
            current.Dispose();
        }
    }

    private void ApplyPrimaryState(HistorySearchState loaded, bool establishStatusBaseline)
    {
        var current = loaded.Workspaces.FirstOrDefault(option => string.Equals(
            option.Id,
            _currentWorkspaceId,
            StringComparison.OrdinalIgnoreCase));
        _currentWorkspaceName = current?.DisplayName;
        ReconcileCurrentWorkspaceSessionFilter();
        UpdateScopeContext();
        ApplyStatus(loaded.Status, establishStatusBaseline);
    }

    private void ApplyAdvancedState(HistorySearchState loaded)
    {
        var selectedWorkspaceId = SelectedWorkspace?.Id;
        var selectedSessionId = SelectedSession?.Id;
        var selectedProfileId = SelectedProfile?.Id;
        var selectedSpecificWorkspaceWasRemoved = IsSpecificWorkspaceScope
                                                   && selectedWorkspaceId is not null
                                                   && !loaded.Workspaces.Any(option => string.Equals(
                                                       option.Id,
                                                       selectedWorkspaceId,
                                                       StringComparison.OrdinalIgnoreCase));
        _suppressSelectionChanges = true;
        try
        {
            ReplaceOptions(WorkspaceOptions, loaded.Workspaces.Select(static option =>
                new HistoryFilterOptionViewModel(option.Id, option.DisplayName, option.ParentId)));
            _allSessionOptions.Clear();
            _allSessionOptions.AddRange(loaded.Sessions.Select(static option =>
                new HistoryFilterOptionViewModel(option.Id, option.DisplayName, option.ParentId)));
            ReplaceOptions(ProfileOptions, new[] { new HistoryFilterOptionViewModel(string.Empty, "All profiles") }
                .Concat(loaded.Profiles.Select(static option =>
                    new HistoryFilterOptionViewModel(option.Id, option.DisplayName, option.ParentId))));
            var preferredWorkspaceId = SelectedScope?.Id == CurrentWorkspaceScopeId
                ? _currentWorkspaceId
                : selectedWorkspaceId;
            SelectedWorkspace = WorkspaceOptions.FirstOrDefault(option => string.Equals(
                                    option.Id,
                                    preferredWorkspaceId,
                                    StringComparison.OrdinalIgnoreCase))
                                ?? WorkspaceOptions.FirstOrDefault(option => string.Equals(
                                    option.Id,
                                    selectedWorkspaceId,
                                    StringComparison.OrdinalIgnoreCase))
                                ?? WorkspaceOptions.FirstOrDefault(option => string.Equals(
                                    option.Id,
                                    _currentWorkspaceId,
                                    StringComparison.OrdinalIgnoreCase))
                                ?? WorkspaceOptions.FirstOrDefault();
            SelectedProfile = ProfileOptions.FirstOrDefault(option => string.Equals(
                                  option.Id,
                                  selectedProfileId,
                                  StringComparison.OrdinalIgnoreCase))
                              ?? ProfileOptions.FirstOrDefault();
            RebuildSessionOptions(selectedSpecificWorkspaceWasRemoved ? null : selectedSessionId);
            if (selectedSpecificWorkspaceWasRemoved)
            {
                IncludeChildSessions = true;
            }
            _advancedFiltersLoaded = true;
        }
        finally
        {
            _suppressSelectionChanges = false;
        }
        UpdateScopeContext();
        UpdateDateValidation();
        UpdateActiveFilters();
        if (selectedSpecificWorkspaceWasRemoved)
        {
            ScheduleSearch(TimeSpan.Zero);
        }
    }

    private StatusRefreshKind ApplyStatus(HistorySearchStatus status, bool establishRefreshBaseline)
    {
        var runtimeChanged = !string.IsNullOrWhiteSpace(_appliedStatusRuntimeInstanceId)
                             && !string.Equals(
                                 _appliedStatusRuntimeInstanceId,
                                 status.RuntimeInstanceId,
                                 StringComparison.Ordinal);
        if (runtimeChanged)
        {
            _appliedStatusRevision = -1;
        }
        if (!runtimeChanged
            && string.Equals(_appliedStatusRuntimeInstanceId, status.RuntimeInstanceId, StringComparison.Ordinal)
            && status.Revision < _appliedStatusRevision)
        {
            return StatusRefreshKind.None;
        }

        var previous = _lastRefreshStatus;
        _appliedStatusRuntimeInstanceId = status.RuntimeInstanceId;
        _appliedStatusRevision = status.Revision;
        _lastRefreshStatus = status;
        IsUnavailable = status.Availability == HistorySearchAvailability.Unavailable;
        IndexStatusText = status.Availability is HistorySearchAvailability.Starting
                          || status.Availability == HistorySearchAvailability.Rebuilding
                          && status.TextGeneration is null
            ? "Preparing local history"
            : $"{status.IndexedDocuments:N0} local {(status.IndexedDocuments == 1 ? "item" : "items")}";
        RuntimeNotice = status switch
        {
            { Availability: HistorySearchAvailability.Unavailable } =>
                status.FailureMessage ?? "Local history search is temporarily unavailable.",
            { FailureCode: not null, TextGeneration: not null } =>
                "Using the last safe index. History maintenance will retry automatically.",
            { Availability: HistorySearchAvailability.Rebuilding, TextGeneration: not null } =>
                "Updating history...",
            { PendingChanges: > 0 } => "Updating history...",
            _ => string.Empty,
        };
        NotifyResultStateChanged();

        if (establishRefreshBaseline || previous is null || !_isInitialized)
        {
            return StatusRefreshKind.None;
        }
        if (!string.Equals(previous.RuntimeInstanceId, status.RuntimeInstanceId, StringComparison.Ordinal))
        {
            // AgentAppRuntimeGateway follows this status with one authoritative Runtime-change event.
            return StatusRefreshKind.None;
        }
        var noIndexBecameReady = previous.Availability == HistorySearchAvailability.Rebuilding
                                 && previous.TextGeneration is null
                                 && status.Availability == HistorySearchAvailability.Ready
                                 && status.TextGeneration is not null;
        var generationChanged = status.TextGeneration is not null
                                && previous.TextGeneration != status.TextGeneration;
        var projectionChanged = previous.ProjectionRevision != status.ProjectionRevision;
        var pendingChangesDrained = previous.PendingChanges > 0 && status.PendingChanges == 0;
        return noIndexBecameReady || generationChanged || projectionChanged || pendingChangesDrained
            ? StatusRefreshKind.Search
            : StatusRefreshKind.None;
    }

    private void OnHistoryStatusChanged(HistorySearchStatus status)
        => _ = RunOnUiThreadAsync(() =>
        {
            var refresh = ApplyStatus(status, establishRefreshBaseline: false);
            if (refresh != StatusRefreshKind.None)
            {
                QueueCurrentQueryRefresh(needsPrimaryState: false);
            }
        });

    private void OnHistoryRuntimeChanged() => QueueCurrentQueryRefresh(needsPrimaryState: true);

    private void OnSelectedWorkspaceChanged(string? workspaceId)
    {
        if (_disposed || string.Equals(_currentWorkspaceId, workspaceId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }
        Volatile.Write(ref _contextRefreshPending, 1);
        CancelSafely(Volatile.Read(ref _searchCancellation));
        _ = RunOnUiThreadAsync(() =>
        {
            if (_disposed)
            {
                Volatile.Write(ref _contextRefreshPending, 0);
                return;
            }
            _currentWorkspaceId = workspaceId;
            _currentWorkspaceName = null;
            _dateRangeSnapshot = null;
            InvalidateOutstandingSearch();
            ReconcileCurrentWorkspaceSessionFilter();
            if (!_isInitialized)
            {
                Volatile.Write(ref _contextRefreshPending, 0);
                return;
            }
            QueueCurrentQueryRefresh(needsPrimaryState: true);
        });
    }

    private void CancelAdvancedFilterLoad()
    {
        Interlocked.Increment(ref _advancedStateGeneration);
        var cancellation = Interlocked.Exchange(ref _advancedStateCancellation, null);
        CancelSafely(cancellation);
    }

    private void QueueCurrentQueryRefresh(bool needsPrimaryState)
    {
        var startWorker = false;
        lock (_refreshLock)
        {
            _refreshNeedsPrimaryState |= needsPrimaryState;
            if (!_refreshQueued)
            {
                _refreshQueued = true;
                startWorker = true;
            }
        }
        if (startWorker)
        {
            _ = RunCoalescedRefreshAsync();
        }
    }

    private async Task RunCoalescedRefreshAsync()
    {
        await Task.Yield();
        bool needsPrimaryState;
        lock (_refreshLock)
        {
            needsPrimaryState = _refreshNeedsPrimaryState;
            _refreshNeedsPrimaryState = false;
            _refreshQueued = false;
        }
        if (_disposed || !_isInitialized)
        {
            return;
        }
        if (needsPrimaryState)
        {
            await ResnapshotCurrentContextAsync().ConfigureAwait(false);
        }
        else
        {
            await SearchAsync(append: false, TimeSpan.Zero, _lifetimeToken).ConfigureAwait(false);
        }
    }
}
