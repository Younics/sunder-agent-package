using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Sunder.Package.Agent.Shared.Presentation;

internal enum AdaptiveListDetailRoute
{
    List = 0,
    ExistingDetail,
    NewDetail,
}

internal enum AdaptiveListDetailLayout
{
    Wide = 0,
    Compact,
}

internal enum AdaptiveDetailPhase
{
    None = 0,
    Loading,
    Ready,
    Error,
}

internal readonly record struct AdaptiveDetailTicket<TKey>(
    TKey Key,
    long IntentRevision,
    LatestRequestTicket Request)
    where TKey : notnull;

internal sealed class KeyedAdaptiveListDetailState<TKey, TItem> : INotifyPropertyChanged, IDisposable
    where TKey : notnull
    where TItem : class
{
    private const string DetailRequestChannel = "detail";
    private readonly Func<TItem, TKey> _keySelector;
    private readonly Action<TItem, TItem>? _updateItem;
    private readonly IEqualityComparer<TKey> _keyComparer;
    private readonly LatestRequestCoordinator _detailRequests = new();
    private AdaptiveListDetailRoute _route;
    private AdaptiveListDetailLayout _layout;
    private AdaptiveDetailPhase _detailPhase;
    private TItem? _selectedItem;
    private TKey _routeKey = default!;
    private TKey _readyKey = default!;
    private bool _hasRouteKey;
    private bool _hasReadyKey;
    private Exception? _detailError;
    private long _intentRevision;
    private long _layoutRevision;
    private bool _explicitListIntent;
    private bool _selectionIsAutomatic;
    private bool _isReconcilingItems;
    private readonly HashSet<string> _pendingPropertyNameSet = new(StringComparer.Ordinal);
    private readonly List<string> _pendingPropertyNames = [];
    private int _transitionDepth;
    private bool _disposed;

    public KeyedAdaptiveListDetailState(
        ObservableCollection<TItem> items,
        Func<TItem, TKey> keySelector,
        Action<TItem, TItem>? updateItem = null,
        IEqualityComparer<TKey>? keyComparer = null)
    {
        Items = items ?? throw new ArgumentNullException(nameof(items));
        _keySelector = keySelector ?? throw new ArgumentNullException(nameof(keySelector));
        _updateItem = updateItem;
        _keyComparer = keyComparer ?? EqualityComparer<TKey>.Default;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public event Action<TItem?, TItem?>? SelectionChanging;

    public event Action<TItem?, TItem?>? SelectionChanged;

    public ObservableCollection<TItem> Items { get; }

    public AdaptiveListDetailRoute Route => _route;

    public AdaptiveListDetailLayout Layout => _layout;

    public AdaptiveDetailPhase DetailPhase => _detailPhase;

    public TItem? SelectedItem => _selectedItem;

    public TKey? RouteKey => _hasRouteKey ? _routeKey : default;

    public TKey? ReadyKey => _hasReadyKey ? _readyKey : default;

    public Exception? DetailError => _detailError;

    public long IntentRevision => _intentRevision;

    public long LayoutRevision => _layoutRevision;

    public bool IsSelectionAutomatic => _selectionIsAutomatic;

    public bool IsList => Route == AdaptiveListDetailRoute.List;

    public bool IsExistingDetail => Route == AdaptiveListDetailRoute.ExistingDetail;

    public bool IsNewDetail => Route == AdaptiveListDetailRoute.NewDetail;

    public void SetLayout(AdaptiveListDetailLayout layout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_layout == layout)
        {
            return;
        }

        RunTransition(() =>
        {
            _layout = layout;
            _layoutRevision++;
            OnPropertyChanged(nameof(Layout));
            OnPropertyChanged(nameof(LayoutRevision));
            if (layout == AdaptiveListDetailLayout.Compact
                && Route == AdaptiveListDetailRoute.ExistingDetail
                && IsSelectionAutomatic)
            {
                SetRoute(AdaptiveListDetailRoute.List, default!);
                ResetDetail();
                SetSelectedItem(null);
                SetSelectionAutomatic(false);
            }
            else if (layout == AdaptiveListDetailLayout.Compact && Route == AdaptiveListDetailRoute.List)
            {
                SetSelectedItem(null);
                SetSelectionAutomatic(false);
            }
            else if (layout == AdaptiveListDetailLayout.Wide
                     && Route == AdaptiveListDetailRoute.List
                     && !_explicitListIntent
                     && Items.FirstOrDefault() is { } first)
            {
                SetRoute(AdaptiveListDetailRoute.ExistingDetail, _keySelector(first));
                ResetDetail();
                SetSelectedItem(first);
                SetSelectionAutomatic(true);
            }
            else if (Route == AdaptiveListDetailRoute.ExistingDetail)
            {
                EnsureExistingRouteResolves(selectFallback: false);
            }
        });
    }

    public long ShowList()
    {
        if (_isReconcilingItems)
        {
            return IntentRevision;
        }

        RunTransition(() =>
        {
            BeginIntent();
            _explicitListIntent = true;
            SetRoute(AdaptiveListDetailRoute.List, default!);
            ResetDetail();
            SetSelectedItem(null);
            SetSelectionAutomatic(false);
        });
        return IntentRevision;
    }

    public long ShowNewDetail(TItem? draftItem = null)
    {
        if (_isReconcilingItems)
        {
            return IntentRevision;
        }

        RunTransition(() =>
        {
            BeginIntent();
            _explicitListIntent = false;
            SetRoute(AdaptiveListDetailRoute.NewDetail, default!);
            ResetDetail();
            SetSelectedItem(ResolveCurrentItem(draftItem));
            SetSelectionAutomatic(false);
        });
        return IntentRevision;
    }

    public long ShowExistingDetail(TItem item)
    {
        ArgumentNullException.ThrowIfNull(item);
        return ShowExistingDetail(_keySelector(item));
    }

    public long ShowExistingDetail(TKey key)
    {
        if (_isReconcilingItems)
        {
            return IntentRevision;
        }

        var item = FindItem(key)
            ?? throw new InvalidOperationException("The requested detail item is not in the current collection.");
        if (Route == AdaptiveListDetailRoute.ExistingDetail
            && _hasRouteKey
            && _keyComparer.Equals(_routeKey, key)
            && ReferenceEquals(SelectedItem, item))
        {
            PromoteSelectionToExplicit();
            return IntentRevision;
        }

        RunTransition(() =>
        {
            BeginIntent();
            _explicitListIntent = false;
            SetRoute(AdaptiveListDetailRoute.ExistingDetail, key);
            ResetDetail();
            SetSelectedItem(item);
            SetSelectionAutomatic(false);
        });
        return IntentRevision;
    }

    public bool TryShowCreatedDetail(TKey key, long intentRevision)
    {
        if (_isReconcilingItems
            || _disposed
            || intentRevision != IntentRevision
            || Route != AdaptiveListDetailRoute.NewDetail
            || FindItem(key) is not { } item)
        {
            return false;
        }

        RunTransition(() =>
        {
            _explicitListIntent = false;
            SetRoute(AdaptiveListDetailRoute.ExistingDetail, key);
            ResetDetail();
            SetSelectedItem(item);
            SetSelectionAutomatic(false);
        });
        return true;
    }

    public void PromoteSelectionToExplicit()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Route != AdaptiveListDetailRoute.ExistingDetail || !IsSelectionAutomatic)
        {
            return;
        }

        RunTransition(() => SetSelectionAutomatic(false));
    }

    public AdaptiveDetailTicket<TKey> BeginDetailLoad(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (Route != AdaptiveListDetailRoute.ExistingDetail
            || !_hasRouteKey
            || FindItem(_routeKey) is null)
        {
            throw new InvalidOperationException("An existing current item must be routed before loading detail.");
        }

        var request = _detailRequests.Begin(DetailRequestChannel, cancellationToken);
        var ticket = new AdaptiveDetailTicket<TKey>(_routeKey, IntentRevision, request);
        RunTransition(() =>
            SetDetailState(AdaptiveDetailPhase.Loading, hasReadyKey: false, default!, null));
        return ticket;
    }

    public bool IsCurrentDetail(AdaptiveDetailTicket<TKey> ticket)
        => !_disposed
           && ticket.IntentRevision == IntentRevision
           && Route == AdaptiveListDetailRoute.ExistingDetail
           && _hasRouteKey
           && _keyComparer.Equals(_routeKey, ticket.Key)
           && _detailRequests.IsCurrent(ticket.Request);

    public bool TrySetDetailReady(AdaptiveDetailTicket<TKey> ticket)
    {
        if (!IsCurrentDetail(ticket))
        {
            return false;
        }

        RunTransition(() =>
            SetDetailState(AdaptiveDetailPhase.Ready, hasReadyKey: true, ticket.Key, null));
        _detailRequests.Complete(ticket.Request);
        return true;
    }

    public bool TrySetDetailError(AdaptiveDetailTicket<TKey> ticket, Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        if (!IsCurrentDetail(ticket))
        {
            return false;
        }

        RunTransition(() =>
            SetDetailState(AdaptiveDetailPhase.Error, hasReadyKey: false, default!, error));
        _detailRequests.Complete(ticket.Request);
        return true;
    }

    public bool TryCancelDetailLoad(AdaptiveDetailTicket<TKey> ticket)
    {
        if (_disposed
            || ticket.IntentRevision != IntentRevision
            || Route != AdaptiveListDetailRoute.ExistingDetail
            || !_hasRouteKey
            || !_keyComparer.Equals(_routeKey, ticket.Key)
            || !_detailRequests.IsLatest(ticket.Request))
        {
            return false;
        }

        RunTransition(() =>
            SetDetailState(AdaptiveDetailPhase.None, hasReadyKey: false, default!, null));
        _detailRequests.Complete(ticket.Request);
        return true;
    }

    public void Reconcile(
        IReadOnlyList<TItem> snapshot,
        bool selectFirstWhenUnrouted = true)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(snapshot);
        ValidateDistinctKeys(snapshot);

        _isReconcilingItems = true;
        try
        {
            for (var desiredIndex = 0; desiredIndex < snapshot.Count; desiredIndex++)
            {
                var incoming = snapshot[desiredIndex];
                var key = _keySelector(incoming);
                var currentIndex = FindIndex(key);
                TItem current;
                if (currentIndex < 0)
                {
                    Items.Insert(desiredIndex, incoming);
                    current = incoming;
                }
                else
                {
                    current = Items[currentIndex];
                    if (_updateItem is not null)
                    {
                        _updateItem(current, incoming);
                    }
                    else if (!EqualityComparer<TItem>.Default.Equals(current, incoming))
                    {
                        Items[currentIndex] = incoming;
                        current = incoming;
                    }

                    currentIndex = FindIndex(key);
                    if (currentIndex != desiredIndex)
                    {
                        Items.Move(currentIndex, desiredIndex);
                    }
                }
            }

            while (Items.Count > snapshot.Count)
            {
                Items.RemoveAt(Items.Count - 1);
            }
        }
        finally
        {
            _isReconcilingItems = false;
        }

        RunTransition(() =>
        {
            switch (Route)
            {
                case AdaptiveListDetailRoute.ExistingDetail:
                    EnsureExistingRouteResolves(selectFallback: Layout == AdaptiveListDetailLayout.Wide);
                    break;
                case AdaptiveListDetailRoute.NewDetail:
                    SetSelectedItem(ResolveCurrentItem(SelectedItem));
                    break;
                case AdaptiveListDetailRoute.List:
                    SetSelectedItem(null);
                    if (selectFirstWhenUnrouted
                        && !_explicitListIntent
                        && Layout == AdaptiveListDetailLayout.Wide
                        && Items.FirstOrDefault() is { } first)
                    {
                        SetRoute(AdaptiveListDetailRoute.ExistingDetail, _keySelector(first));
                        ResetDetail();
                        SetSelectedItem(first);
                        SetSelectionAutomatic(true);
                    }
                    break;
            }
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _detailRequests.Dispose();
    }

    private void EnsureExistingRouteResolves(bool selectFallback)
    {
        var selected = !_hasRouteKey ? null : FindItem(_routeKey);
        if (selected is not null)
        {
            SetSelectedItem(selected);
            return;
        }

        if (selectFallback && !_explicitListIntent && Items.FirstOrDefault() is { } first)
        {
            SetRoute(AdaptiveListDetailRoute.ExistingDetail, _keySelector(first));
            ResetDetail();
            SetSelectedItem(first);
            SetSelectionAutomatic(true);
            return;
        }

        SetRoute(AdaptiveListDetailRoute.List, default!);
        ResetDetail();
        SetSelectedItem(null);
        SetSelectionAutomatic(false);
    }

    private void BeginIntent()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _intentRevision++;
        OnPropertyChanged(nameof(IntentRevision));
    }

    private void ResetDetail()
    {
        _detailRequests.Invalidate(DetailRequestChannel);
        SetDetailState(AdaptiveDetailPhase.None, hasReadyKey: false, default!, null);
    }

    private void SetDetailState(
        AdaptiveDetailPhase phase,
        bool hasReadyKey,
        TKey readyKey,
        Exception? error)
    {
        if (phase == AdaptiveDetailPhase.Ready
            && (Route != AdaptiveListDetailRoute.ExistingDetail
                || !_hasRouteKey
                || !hasReadyKey
                || !_keyComparer.Equals(_routeKey, readyKey)))
        {
            throw new InvalidOperationException("Ready detail must match the existing-detail route key.");
        }

        if (_detailPhase != phase)
        {
            _detailPhase = phase;
            OnPropertyChanged(nameof(DetailPhase));
        }
        if (_hasReadyKey != hasReadyKey
            || hasReadyKey && !_keyComparer.Equals(_readyKey, readyKey))
        {
            _readyKey = readyKey;
            _hasReadyKey = hasReadyKey;
            OnPropertyChanged(nameof(ReadyKey));
        }
        if (!ReferenceEquals(_detailError, error))
        {
            _detailError = error;
            OnPropertyChanged(nameof(DetailError));
        }
    }

    private void SetRoute(AdaptiveListDetailRoute route, TKey key)
    {
        var hasKey = route == AdaptiveListDetailRoute.ExistingDetail;
        var routeChanged = _route != route;
        var keyChanged = _hasRouteKey != hasKey
                         || hasKey && !_keyComparer.Equals(_routeKey, key);
        _route = route;
        _routeKey = key;
        _hasRouteKey = hasKey;
        if (routeChanged)
        {
            OnPropertyChanged(nameof(Route));
            OnPropertyChanged(nameof(IsList));
            OnPropertyChanged(nameof(IsExistingDetail));
            OnPropertyChanged(nameof(IsNewDetail));
        }
        if (keyChanged)
        {
            OnPropertyChanged(nameof(RouteKey));
        }
    }

    private void SetSelectedItem(TItem? item)
    {
        if (ReferenceEquals(_selectedItem, item))
        {
            return;
        }

        var previous = _selectedItem;
        SelectionChanging?.Invoke(previous, item);
        _selectedItem = item;
        OnPropertyChanged(nameof(SelectedItem));
        SelectionChanged?.Invoke(previous, item);
    }

    private void SetSelectionAutomatic(bool value)
    {
        if (_selectionIsAutomatic == value)
        {
            return;
        }

        _selectionIsAutomatic = value;
        OnPropertyChanged(nameof(IsSelectionAutomatic));
    }

    private TItem? ResolveCurrentItem(TItem? item)
        => item is null ? null : FindItem(_keySelector(item));

    private TItem? FindItem(TKey key)
    {
        var index = FindIndex(key);
        return index < 0 ? null : Items[index];
    }

    private int FindIndex(TKey key)
    {
        for (var index = 0; index < Items.Count; index++)
        {
            if (_keyComparer.Equals(_keySelector(Items[index]), key))
            {
                return index;
            }
        }

        return -1;
    }

    private void ValidateDistinctKeys(IReadOnlyList<TItem> snapshot)
    {
        var keys = new HashSet<TKey>(_keyComparer);
        foreach (var item in snapshot)
        {
            if (!keys.Add(_keySelector(item)))
            {
                throw new InvalidOperationException("The list snapshot contains a duplicate stable key.");
            }
        }
    }

    private void ValidateInvariants()
    {
        if (Layout == AdaptiveListDetailLayout.Compact
            && Route == AdaptiveListDetailRoute.List
            && SelectedItem is not null)
        {
            throw new InvalidOperationException("Compact list route cannot retain an existing selection.");
        }
        if (Route == AdaptiveListDetailRoute.ExistingDetail
            && (!_hasRouteKey
                || SelectedItem is null
                || !_keyComparer.Equals(_routeKey, _keySelector(SelectedItem))
                || FindItem(_routeKey) is not { } current
                || !ReferenceEquals(current, SelectedItem)))
        {
            throw new InvalidOperationException("Existing detail must resolve to the selected current collection item.");
        }
        if (DetailPhase == AdaptiveDetailPhase.Ready
            && (Route != AdaptiveListDetailRoute.ExistingDetail
                || !_hasRouteKey
                || !_hasReadyKey
                || !_keyComparer.Equals(_routeKey, _readyKey)))
        {
            throw new InvalidOperationException("Ready detail key does not match the route key.");
        }
        if (IsSelectionAutomatic && Route != AdaptiveListDetailRoute.ExistingDetail)
        {
            throw new InvalidOperationException("Only an existing-detail selection can be automatic.");
        }
    }

    private void RunTransition(Action transition)
    {
        _transitionDepth++;
        try
        {
            transition();
            ValidateInvariants();
        }
        finally
        {
            _transitionDepth--;
            if (_transitionDepth == 0)
            {
                FlushPropertyChanges();
            }
        }
    }

    private void FlushPropertyChanges()
    {
        if (_pendingPropertyNames.Count == 0)
        {
            return;
        }

        var propertyNames = _pendingPropertyNames.ToArray();
        _pendingPropertyNames.Clear();
        _pendingPropertyNameSet.Clear();
        foreach (var propertyName in propertyNames)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        if (propertyName is null)
        {
            return;
        }
        if (_transitionDepth > 0)
        {
            if (_pendingPropertyNameSet.Add(propertyName))
            {
                _pendingPropertyNames.Add(propertyName);
            }
            return;
        }

        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
