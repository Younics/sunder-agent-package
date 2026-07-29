using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentHistorySearchViewModel : ObservableObject, IDisposable
{
    private const string ChatViewId = "sunder.package.agent.chat";
    private const string SubsessionsViewId = "sunder.package.agent.subagents.sessions";
    private const string CurrentWorkspaceScopeId = "current";
    private const string AllWorkspacesScopeId = "all";
    private const string SpecificWorkspaceScopeId = "specific";
    private const string MissingWorkspaceScopeId = "history:no-workspace";
    private const string AnyTimeId = "any";
    private const string TodayId = "today";
    private const string PastSevenDaysId = "past-7-days";
    private const string PastThirtyDaysId = "past-30-days";
    private const string PastYearId = "past-year";
    private const string CustomRangeId = "custom";
    private static readonly TimeSpan QueryDebounce = TimeSpan.FromMilliseconds(300);

    private readonly IAgentHistorySearchGateway _gateway;
    private readonly AgentChatSelectionStateService _selectionState;
    private readonly IPackageShellViewService _shellViewService;
    private readonly TimeProvider _timeProvider;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly CancellationToken _lifetimeToken;
    private readonly AsyncOnce _initialization = new();
    private readonly object _refreshLock = new();
    private readonly List<HistoryFilterOptionViewModel> _allSessionOptions = [];
    private ReplacementCancellation? _searchCancellation;
    private ReplacementCancellation? _stateCancellation;
    private ReplacementCancellation? _advancedStateCancellation;
    private int _searchGeneration;
    private int _stateGeneration;
    private int _advancedStateGeneration;
    private int _contextRefreshPending;
    private long _appliedStatusRevision = -1;
    private string? _appliedStatusRuntimeInstanceId;
    private HistorySearchStatus? _lastRefreshStatus;
    private string? _continuation;
    private (DateTimeOffset? FromUtc, DateTimeOffset? ToUtc)? _dateRangeSnapshot;
    private string? _currentWorkspaceId;
    private string? _currentWorkspaceName;
    private bool _suppressSelectionChanges;
    private bool _advancedFiltersLoaded;
    private bool _refreshQueued;
    private bool _refreshNeedsPrimaryState;
    private bool _isInitialized;
    private bool _disposed;

    internal AgentHistorySearchViewModel(
        IAgentHistorySearchGateway gateway,
        AgentChatSelectionStateService selectionState,
        IPackageShellViewService shellViewService,
        TimeProvider? timeProvider = null)
    {
        _gateway = gateway;
        _selectionState = selectionState;
        _shellViewService = shellViewService;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lifetimeToken = _lifetime.Token;

        _suppressSelectionChanges = true;
        ScopeOptions.Add(new HistoryChoiceViewModel(CurrentWorkspaceScopeId, "Current workspace"));
        ScopeOptions.Add(new HistoryChoiceViewModel(AllWorkspacesScopeId, "All workspaces"));
        ScopeOptions.Add(new HistoryChoiceViewModel(SpecificWorkspaceScopeId, "Specific workspace"));
        SelectedScope = ScopeOptions[0];

        RoleOptions.Add(new HistoryChoiceViewModel("Any", "Any role"));
        RoleOptions.Add(new HistoryChoiceViewModel("User", "User"));
        RoleOptions.Add(new HistoryChoiceViewModel("Assistant", "Assistant"));
        SelectedRole = RoleOptions[0];

        ActivityOptions.Add(new HistoryChoiceViewModel("None", "Any activity"));
        foreach (var value in Enum.GetValues<HistoryActivityKind>().Where(static value => value != HistoryActivityKind.None))
        {
            ActivityOptions.Add(new HistoryChoiceViewModel(value.ToString(), value.ToString()));
        }
        SelectedActivity = ActivityOptions[0];

        WhenOptions.Add(new HistoryChoiceViewModel(AnyTimeId, "Any time"));
        WhenOptions.Add(new HistoryChoiceViewModel(TodayId, "Today"));
        WhenOptions.Add(new HistoryChoiceViewModel(PastSevenDaysId, "Past 7 days"));
        WhenOptions.Add(new HistoryChoiceViewModel(PastThirtyDaysId, "Past 30 days"));
        WhenOptions.Add(new HistoryChoiceViewModel(PastYearId, "Past year"));
        WhenOptions.Add(new HistoryChoiceViewModel(CustomRangeId, "Custom range"));
        SelectedWhen = WhenOptions[0];
        _suppressSelectionChanges = false;

        _gateway.HistoryStatusChanged += OnHistoryStatusChanged;
        _gateway.HistoryRuntimeChanged += OnHistoryRuntimeChanged;
        _selectionState.SelectedWorkspaceChanged += OnSelectedWorkspaceChanged;
        _gateway.StartObservingHistoryStatus();
    }

    public ObservableCollection<AgentHistorySearchResultViewModel> Results { get; } = [];
    public ObservableCollection<HistoryFilterOptionViewModel> WorkspaceOptions { get; } = [];
    public ObservableCollection<HistoryFilterOptionViewModel> SessionOptions { get; } = [];
    public ObservableCollection<HistoryFilterOptionViewModel> ProfileOptions { get; } = [];
    public ObservableCollection<HistoryChoiceViewModel> ScopeOptions { get; } = [];
    public ObservableCollection<HistoryChoiceViewModel> RoleOptions { get; } = [];
    public ObservableCollection<HistoryChoiceViewModel> ActivityOptions { get; } = [];
    public ObservableCollection<HistoryChoiceViewModel> WhenOptions { get; } = [];
    public ObservableCollection<HistoryActiveFilterChipViewModel> ActiveFilters { get; } = [];

    [ObservableProperty] private string _queryText = string.Empty;
    [ObservableProperty] private HistoryChoiceViewModel? _selectedScope;
    [ObservableProperty] private HistoryFilterOptionViewModel? _selectedWorkspace;
    [ObservableProperty] private HistoryFilterOptionViewModel? _selectedSession;
    [ObservableProperty] private HistoryFilterOptionViewModel? _selectedProfile;
    [ObservableProperty] private HistoryChoiceViewModel? _selectedRole;
    [ObservableProperty] private HistoryChoiceViewModel? _selectedActivity;
    [ObservableProperty] private HistoryChoiceViewModel? _selectedWhen;
    [ObservableProperty] private DateTimeOffset? _fromDate;
    [ObservableProperty] private DateTimeOffset? _toDate;
    [ObservableProperty] private bool _includeChildSessions = true;
    [ObservableProperty] private bool _isAdvancedExpanded;
    [ObservableProperty] private bool _isInitialLoading = true;
    [ObservableProperty] private bool _isSearching;
    [ObservableProperty] private bool _isAdvancedLoading;
    [ObservableProperty] private bool _isPartial;
    [ObservableProperty] private bool _isUnavailable;
    [ObservableProperty] private bool _isCompactLayout = true;
    [ObservableProperty] private string _runtimeNotice = string.Empty;
    [ObservableProperty] private string _searchError = string.Empty;
    [ObservableProperty] private string _navigationError = string.Empty;
    [ObservableProperty] private string _indexStatusText = "Preparing local history";
    [ObservableProperty] private string _scopeContextText = "Current workspace";
    [ObservableProperty] private string _dateValidationMessage = string.Empty;

    public bool HasQuery => !string.IsNullOrWhiteSpace(QueryText);
    public bool HasResults => Results.Count > 0;
    public bool HasNoResults => !HasResults;
    public bool HasSearchError => !string.IsNullOrWhiteSpace(SearchError);
    public bool HasNavigationError => !string.IsNullOrWhiteSpace(NavigationError);
    public bool HasRuntimeNotice => !string.IsNullOrWhiteSpace(RuntimeNotice);
    public bool HasDateValidation => !string.IsNullOrWhiteSpace(DateValidationMessage);
    public bool HasActiveFilters => ActiveFilters.Count > 0;
    public bool IsSearchProgressActive => IsInitialLoading || IsSearching;
    public bool IsSpecificWorkspaceScope => SelectedScope?.Id == SpecificWorkspaceScopeId;
    public bool IsSpecificSessionSelected => Guid.TryParse(SelectedSession?.Id, out _);
    public bool IsCustomRange => SelectedWhen?.Id == CustomRangeId;
    public bool ShowEmptyState => _isInitialized
                                  && !IsInitialLoading
                                  && !IsSearching
                                  && !IsUnavailable
                                  && !HasSearchError
                                  && HasNoResults;
    public bool CanLoadMore => !IsSearching && _continuation is not null;
    public string AdvancedChevron => IsAdvancedExpanded ? "▴" : "▾";
    public string EmptyTitle => HasQuery
        ? "No matches"
        : HasActiveFilters ? "No history matches these filters" : "No recent history";
    public string EmptyDescription => HasQuery
        ? "Try a broader phrase or remove a filter."
        : HasActiveFilters
            ? "Try removing a filter to include more local history."
            : SelectedScope?.Id == AllWorkspacesScopeId
                ? "Messages and safe activity from your workspaces will appear here."
                : "Messages and safe activity from this workspace will appear here.";
    public string AdvancedAutomationHelpText => IsAdvancedExpanded
        ? "Hide workspace, content, and time filters."
        : "Show workspace, content, and time filters.";

    internal Task InitializeAsync(CancellationToken cancellationToken = default)
        => _initialization.RunAsync(InitializeCoreAsync, cancellationToken);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _gateway.HistoryStatusChanged -= OnHistoryStatusChanged;
        _gateway.HistoryRuntimeChanged -= OnHistoryRuntimeChanged;
        _selectionState.SelectedWorkspaceChanged -= OnSelectedWorkspaceChanged;
        CancelSafely(Interlocked.Exchange(ref _searchCancellation, null));
        CancelSafely(Interlocked.Exchange(ref _stateCancellation, null));
        CancelSafely(Interlocked.Exchange(ref _advancedStateCancellation, null));
        _lifetime.Cancel();
        _initialization.Dispose();
        _lifetime.Dispose();
    }

    private static void ReplaceOptions<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static async Task RunOnUiThreadAsync(Action action)
    {
        if (Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(action, DispatcherPriority.Background);
    }

    private static void CancelSafely(ReplacementCancellation? cancellation)
        => cancellation?.Cancel();

    private sealed class ReplacementCancellation : IDisposable
    {
        private readonly object _lock = new();
        private readonly CancellationTokenSource _source;
        private bool _disposed;

        private ReplacementCancellation(CancellationTokenSource source)
        {
            _source = source;
        }

        internal CancellationToken Token => _source.Token;
        internal bool IsCancellationRequested => _source.IsCancellationRequested;

        internal static ReplacementCancellation CreateLinked(params CancellationToken[] tokens)
            => new(CancellationTokenSource.CreateLinkedTokenSource(tokens));

        internal void Cancel()
        {
            lock (_lock)
            {
                if (!_disposed)
                {
                    _source.Cancel();
                }
            }
        }

        public void Dispose()
        {
            lock (_lock)
            {
                if (_disposed)
                {
                    return;
                }
                _disposed = true;
                _source.Dispose();
            }
        }
    }
}
