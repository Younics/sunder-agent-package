using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.HistorySearch;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentHistorySearchViewModel
{
    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        try
        {
            _currentWorkspaceId = await _selectionState.GetSelectedWorkspaceIdAsync(cancellationToken)
                .ConfigureAwait(false);
            var state = await LoadPrimaryStateAsync(cancellationToken).ConfigureAwait(false);
            await RunOnUiThreadAsync(() => ApplyPrimaryState(state, establishStatusBaseline: true))
                .ConfigureAwait(false);
            _isInitialized = true;
            await SearchAsync(append: false, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                IsInitialLoading = false;
                NotifyResultStateChanged();
            }).ConfigureAwait(false);
        }
    }

    [RelayCommand]
    private async Task SearchNowAsync()
    {
        _dateRangeSnapshot = null;
        await SearchAsync(append: false, TimeSpan.Zero, _lifetimeToken).ConfigureAwait(false);
    }

    [RelayCommand(CanExecute = nameof(CanLoadMore))]
    private async Task LoadMoreAsync()
        => await SearchAsync(append: true, TimeSpan.Zero, _lifetimeToken).ConfigureAwait(false);

    [RelayCommand]
    private async Task ClearQueryAsync()
    {
        if (!HasQuery)
        {
            return;
        }
        _suppressSelectionChanges = true;
        QueryText = string.Empty;
        _suppressSelectionChanges = false;
        _dateRangeSnapshot = null;
        NotifyQueryStateChanged();
        await SearchAsync(append: false, TimeSpan.Zero, _lifetimeToken).ConfigureAwait(false);
    }

    [RelayCommand]
    private async Task EscapeAsync()
    {
        if (HasQuery)
        {
            await ClearQueryAsync().ConfigureAwait(false);
            return;
        }
        if (IsAdvancedExpanded)
        {
            IsAdvancedExpanded = false;
        }
    }

    [RelayCommand]
    private async Task OpenResultAsync(AgentHistorySearchResultViewModel? result)
    {
        if (result is null)
        {
            return;
        }
        NavigationError = string.Empty;
        try
        {
            var opened = await _shellViewService.OpenViewPanelAsync(
                result.IsChildSession ? SubsessionsViewId : ChatViewId,
                HistorySearchNavigation.ToParameters(result.Hit),
                _lifetimeToken);
            if (!opened)
            {
                NavigationError = result.IsChildSession
                    ? "The Subsessions view is unavailable. Your search results are unchanged."
                    : "Agent Chat is unavailable. Your search results are unchanged.";
            }
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            NavigationError = result.IsChildSession
                ? "The Subsessions view could not open this result. Your search results are unchanged."
                : "Agent Chat could not open this result. Your search results are unchanged.";
        }
    }

    private void ScheduleSearch(TimeSpan delay)
    {
        if (_suppressSelectionChanges || _disposed)
        {
            return;
        }
        _dateRangeSnapshot = null;
        if (!_isInitialized
            || Volatile.Read(ref _contextRefreshPending) != 0)
        {
            return;
        }
        _ = SearchAsync(append: false, delay, _lifetimeToken);
    }

    private async Task SearchAsync(bool append, TimeSpan delay, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _contextRefreshPending) != 0)
        {
            return;
        }
        var current = ReplacementCancellation.CreateLinked(cancellationToken, _lifetimeToken);
        var previous = Interlocked.Exchange(ref _searchCancellation, current);
        CancelSafely(previous);
        var generation = Interlocked.Increment(ref _searchGeneration);
        var token = current.Token;
        try
        {
            await RunOnUiThreadAsync(() =>
            {
                if (generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }
                IsSearching = true;
                SearchError = string.Empty;
                NotifyResultStateChanged();
            }).ConfigureAwait(false);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, token).ConfigureAwait(false);
            }

            HistorySearchRequest? request = null;
            await RunOnUiThreadAsync(() => request = CreateRequest(append ? _continuation : null))
                .ConfigureAwait(false);
            var response = await _gateway.SearchHistoryAsync(request!, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await RunOnUiThreadAsync(() =>
            {
                if (generation != Volatile.Read(ref _searchGeneration))
                {
                    return;
                }
                if (!append || response.Restarted)
                {
                    Results.Clear();
                }
                foreach (var hit in response.Results)
                {
                    Results.Add(new AgentHistorySearchResultViewModel(hit));
                }
                _continuation = response.Continuation;
                IsPartial = response.IsPartial;
                ApplyStatus(response.Status, establishRefreshBaseline: false);
                NotifyResultStateChanged();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception) when (!_lifetime.IsCancellationRequested)
        {
            await RunOnUiThreadAsync(() =>
            {
                if (generation == Volatile.Read(ref _searchGeneration))
                {
                    SearchError = HasResults
                        ? "History search could not complete. Your previous results are still shown."
                        : "History search could not complete. Try again.";
                    NotifyResultStateChanged();
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await RunOnUiThreadAsync(() =>
            {
                if (generation == Volatile.Read(ref _searchGeneration))
                {
                    IsSearching = false;
                    NotifyResultStateChanged();
                }
            }).ConfigureAwait(false);
            Interlocked.CompareExchange(ref _searchCancellation, null, current);
            current.Dispose();
        }
    }

    private void InvalidateOutstandingSearch()
    {
        Interlocked.Increment(ref _searchGeneration);
        var cancellation = Interlocked.Exchange(ref _searchCancellation, null);
        CancelSafely(cancellation);
        _continuation = null;
        IsSearching = false;
        NotifyResultStateChanged();
    }

    private void NotifyQueryStateChanged()
    {
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(EmptyTitle));
        OnPropertyChanged(nameof(EmptyDescription));
        NotifyResultStateChanged();
    }

    private void NotifyResultStateChanged()
    {
        OnPropertyChanged(nameof(HasResults));
        OnPropertyChanged(nameof(HasNoResults));
        OnPropertyChanged(nameof(HasSearchError));
        OnPropertyChanged(nameof(HasNavigationError));
        OnPropertyChanged(nameof(HasRuntimeNotice));
        OnPropertyChanged(nameof(IsSearchProgressActive));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(CanLoadMore));
        LoadMoreCommand.NotifyCanExecuteChanged();
    }
}
