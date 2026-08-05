using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed partial class MemoryInspectorViewModel : ObservableObject, IDisposable
{
    private const string SessionRefreshChannel = "memory-sessions";
    private const string WorkerStatusRefreshChannel = "memory-worker-status";

    private readonly IMemoryInspectorGateway _memoryInspectorService;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly PresentationTaskScope _tasks;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncOnce _initialization = new();
    private readonly LatestRequestCoordinator _requests = new();
    private SemanticEmbeddingContext? _selectedSessionSemanticContext;
    private CancellationTokenSource? _sessionLoadCancellation;
    private bool _suppressSessionSelectionHandlers;
    private bool _suppressMemorySelectionDetails;
    private bool _initializationSessionRefreshPending;
    private bool _initializationWorkerRefreshPending;
    private bool _isInitialized;
    private bool _disposed;
    private Task _currentSessionRefresh = Task.CompletedTask;
    private int _sessionLoadVersion;
    private int _memoryDetailsLoadVersion;
    private int _busyOperationCount;
    private long _sessionSelectionRevision;
    private long _memorySelectionRevision;

    internal MemoryInspectorViewModel(IMemoryInspectorGateway memoryInspectorService)
        : this(memoryInspectorService, PresentationDispatcher.Capture())
    {
    }

    internal MemoryInspectorViewModel(
        IMemoryInspectorGateway memoryInspectorService,
        IPresentationDispatcher uiDispatcher)
    {
        _memoryInspectorService = memoryInspectorService;
        _uiDispatcher = uiDispatcher;
        _tasks = new PresentationTaskScope(exception => _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                StatusText = exception.Message;
            }
        }));
        _memoryInspectorService.SessionChanged += OnSessionChanged;
        _memoryInspectorService.SemanticWorkerStatusChanged += OnSemanticWorkerStatusChanged;
    }

    public ObservableCollection<AgentSessionRecord> Sessions { get; } = [];

    public ObservableCollection<MemoryListItemViewModel> Memories { get; } = [];

    public ObservableCollection<MemoryEvidenceItemViewModel> EvidenceItems { get; } = [];

    public ObservableCollection<MemoryListItemViewModel> SupersededMemoryItems { get; } = [];

    public IReadOnlyList<string> CategoryOptions { get; } =
    [
        "participant-fact",
        "preference",
        "standing-instruction",
        "remembered-fact",
        "project-fact",
        "environment-fact",
    ];

    [ObservableProperty]
    private AgentSessionRecord? _selectedSession;

    [ObservableProperty]
    private MemoryListItemViewModel? _selectedMemory;

    [ObservableProperty]
    private MemoryListItemViewModel? _supersedingMemory;

    [ObservableProperty]
    private string _searchText = string.Empty;

    [ObservableProperty]
    private bool _includeInactive;

    [ObservableProperty]
    private string _workingSummaryText = string.Empty;

    [ObservableProperty]
    private string _editCategory = string.Empty;

    [ObservableProperty]
    private string _editContent = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private string _semanticStatusText = string.Empty;

    [ObservableProperty]
    private string _semanticWorkerStatusText = string.Empty;

    [ObservableProperty]
    private bool _hasSemanticWorkerFailure;

    [ObservableProperty]
    private string _memoryMetricsSummaryText = string.Empty;

    [ObservableProperty]
    private string _correctionLineageSummaryText = string.Empty;

    [ObservableProperty]
    private bool _canReindexSemanticIndex;

    [ObservableProperty]
    private bool _isBusy;

    public bool HasSelection => SelectedMemory is not null;

    public bool HasNoSelection => !HasSelection;

    public bool HasSupersedingMemory => SupersedingMemory is not null;

    public bool HasSupersededMemories => SupersededMemoryItems.Count > 0;

    public bool HasWorkingSummary => !string.IsNullOrWhiteSpace(WorkingSummaryText);

    public bool HasSemanticWorkerStatus => !string.IsNullOrWhiteSpace(SemanticWorkerStatusText);

    public bool HasMemoryMetricsSummary => !string.IsNullOrWhiteSpace(MemoryMetricsSummaryText);

    public bool HasCorrectionLineage => !string.IsNullOrWhiteSpace(CorrectionLineageSummaryText);

    public string PinButtonText => SelectedMemory?.IsPinned == true ? "Unpin" : "Pin";

    public string ContestButtonText => SelectedMemory?.State == MemoryLocalStore.ContestedState ? "Keep Contested" : "Contest";

    public bool HasSelectedMemorySemanticStatus => SelectedMemory?.HasSemanticIndexStatus == true;

    public Task InitializeAsync(CancellationToken cancellationToken = default)
        => _initialization.RunAsync(InitializeCoreAsync, cancellationToken);

    internal Task CurrentSessionRefresh => _currentSessionRefresh;

    partial void OnSelectedSessionChanged(AgentSessionRecord? value)
    {
        if (_suppressSessionSelectionHandlers)
        {
            return;
        }

        _sessionSelectionRevision++;
        _tasks.Run(StartSessionLoadAsync(value, preferredMemoryId: null));
    }

    partial void OnSelectedMemoryChanged(MemoryListItemViewModel? value)
    {
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(HasNoSelection));
        OnPropertyChanged(nameof(HasSupersedingMemory));
        OnPropertyChanged(nameof(HasSupersededMemories));
        OnPropertyChanged(nameof(PinButtonText));
        OnPropertyChanged(nameof(ContestButtonText));
        OnPropertyChanged(nameof(HasSelectedMemorySemanticStatus));
        if (_suppressMemorySelectionDetails)
        {
            return;
        }
        _memorySelectionRevision++;
        StartMemoryDetailsLoad(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        _tasks.Run(StartSessionLoadAsync(SelectedSession, SelectedMemory?.MemoryId));
    }

    partial void OnWorkingSummaryTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasWorkingSummary));
    }

    partial void OnIncludeInactiveChanged(bool value)
    {
        _tasks.Run(StartSessionLoadAsync(SelectedSession, SelectedMemory?.MemoryId));
    }

    [RelayCommand]
    private async Task RefreshAsync()
    {
        await StartSessionRefresh(cancellationToken: _lifetime.Token);
    }

    [RelayCommand]
    private async Task ReindexSemanticIndexAsync()
    {
        if (SelectedSession is null)
        {
            StatusText = "Select a session first.";
            return;
        }

        BeginBusy();
        try
        {
            var result = await _memoryInspectorService.ReindexSessionAsync(SelectedSession.SessionId);
            StatusText = result.Message;
            await StartSessionLoadAsync(SelectedSession, SelectedMemory?.MemoryId);
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private async Task SaveMemoryAsync()
    {
        if (SelectedMemory is null)
        {
            StatusText = "Select a memory first.";
            return;
        }

        var content = EditContent.Trim();
        var category = EditCategory.Trim();
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(category))
        {
            StatusText = "Category and content are required.";
            return;
        }

        try
        {
            await _memoryInspectorService.UpdateMemoryAsync(
                SelectedMemory.MemoryId,
                category,
                content,
                "Corrected in memory inspector.",
                _lifetime.Token);
            StatusText = "Memory saved.";
            await StartSessionLoadAsync(SelectedSession, SelectedMemory.MemoryId);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private async Task TogglePinnedAsync()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        await _memoryInspectorService.SetPinnedAsync(
            SelectedMemory.MemoryId,
            !SelectedMemory.IsPinned,
            _lifetime.Token);
        StatusText = SelectedMemory.IsPinned ? "Memory unpinned." : "Memory pinned.";
        await StartSessionLoadAsync(SelectedSession, SelectedMemory.MemoryId);
    }

    [RelayCommand]
    private async Task ContestMemoryAsync()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        await _memoryInspectorService.ContestMemoryAsync(SelectedMemory.MemoryId, _lifetime.Token);
        StatusText = "Memory marked as contested.";
        await StartSessionLoadAsync(SelectedSession, SelectedMemory.MemoryId);
    }

    [RelayCommand]
    private async Task ForgetMemoryAsync()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        await _memoryInspectorService.ForgetMemoryAsync(SelectedMemory.MemoryId, _lifetime.Token);
        StatusText = "Memory forgotten.";
        await StartSessionLoadAsync(SelectedSession, preferredMemoryId: null);
    }

    [RelayCommand]
    private async Task SupersedeMemoryAsync()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        await _memoryInspectorService.SupersedeMemoryAsync(SelectedMemory.MemoryId, _lifetime.Token);
        StatusText = "Memory superseded.";
        await StartSessionLoadAsync(SelectedSession, preferredMemoryId: null);
    }

    [RelayCommand]
    private async Task CreateCorrectionAsync()
    {
        if (SelectedMemory is null)
        {
            StatusText = "Select a memory first.";
            return;
        }

        var content = EditContent.Trim();
        var category = EditCategory.Trim();
        if (string.IsNullOrWhiteSpace(content) || string.IsNullOrWhiteSpace(category))
        {
            StatusText = "Category and content are required.";
            return;
        }

        try
        {
            var result = await _memoryInspectorService.CreateCorrectedMemoryAsync(
                SelectedMemory.MemoryId,
                category,
                content,
                _lifetime.Token);
            StatusText = result.CreatedNewMemory
                ? "Created corrected memory and linked the original as superseded."
                : "Applied correction to the existing memory.";
            await StartSessionLoadAsync(SelectedSession, result.CorrectedMemory.MemoryId);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _sessionLoadVersion++;
        _memoryDetailsLoadVersion++;
        _lifetime.Cancel();
        _initialization.Dispose();
        _requests.Dispose();
        _sessionLoadCancellation?.Cancel();
        _memoryInspectorService.SessionChanged -= OnSessionChanged;
        _memoryInspectorService.SemanticWorkerStatusChanged -= OnSemanticWorkerStatusChanged;
        _sessionLoadCancellation?.Dispose();
        _sessionLoadCancellation = null;
        _tasks.Dispose();
        _lifetime.Dispose();
    }

    private async Task<MemorySessionContent> CaptureSessionContentAsync(
        Guid sessionId,
        Guid? preferredMemoryId,
        SemanticEmbeddingContext? semanticContext,
        string searchText,
        bool includeInactive,
        CancellationToken cancellationToken)
    {
        var checkpointTask = _memoryInspectorService.GetSessionContextCheckpointAsync(
            sessionId,
            cancellationToken);
        var workingSummaryTask = _memoryInspectorService.GetWorkingSummaryAsync(
            sessionId,
            cancellationToken);
        var memoriesTask = _memoryInspectorService.ListMemoriesAsync(
            sessionId,
            searchText,
            includeInactive,
            cancellationToken);
        await Task.WhenAll(checkpointTask, workingSummaryTask, memoriesTask).ConfigureAwait(false);
        var workingSummary = (await checkpointTask.ConfigureAwait(false))?.SummaryText
            ?? (await workingSummaryTask.ConfigureAwait(false))?.SummaryText
            ?? string.Empty;
        var memories = await Task.WhenAll((await memoriesTask.ConfigureAwait(false)).Select(async memory =>
            new MemoryListItemViewModel(
                memory,
                await _memoryInspectorService.GetSemanticIndexStatusAsync(
                        memory,
                        semanticContext,
                        cancellationToken)
                    .ConfigureAwait(false))));
        var selectedMemory = memories.FirstOrDefault(memory => memory.MemoryId == preferredMemoryId)
            ?? memories.FirstOrDefault();
        return new MemorySessionContent(
            workingSummary,
            memories,
            selectedMemory?.MemoryId,
            await CaptureSelectionDetailsAsync(
                    selectedMemory,
                    semanticContext,
                    cancellationToken)
                .ConfigureAwait(false));
    }

    private void ApplySessionContent(MemorySessionContent content)
    {
        _memoryDetailsLoadVersion++;
        EvidenceItems.Clear();
        EditCategory = string.Empty;
        EditContent = string.Empty;
        WorkingSummaryText = content.WorkingSummary;

        _suppressMemorySelectionDetails = true;
        try
        {
            Memories.Clear();
            SelectedMemory = null;
            foreach (var memory in content.Memories)
            {
                Memories.Add(memory);
            }
            SelectedMemory = Memories.FirstOrDefault(memory => memory.MemoryId == content.SelectedMemoryId);
        }
        finally
        {
            _suppressMemorySelectionDetails = false;
        }
        ApplySelectionDetails(content.SelectionDetails);

        if (Memories.Count == 0)
        {
            StatusText = "No stored memories match the current filter.";
        }
    }

    private Task StartSessionLoadAsync(
        AgentSessionRecord? session,
        Guid? preferredMemoryId,
        LatestRequestTicket? authority = null)
    {
        if (_uiDispatcher.CheckAccess())
        {
            return StartSessionLoadOnPresentationThread(session, preferredMemoryId, authority);
        }

        return StartSessionLoadOnPresentationThreadAsync(session, preferredMemoryId, authority);
    }

    private async Task StartSessionLoadOnPresentationThreadAsync(
        AgentSessionRecord? session,
        Guid? preferredMemoryId,
        LatestRequestTicket? authority)
    {
        Task sessionLoad = Task.CompletedTask;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                sessionLoad = StartSessionLoadOnPresentationThread(
                    session,
                    preferredMemoryId,
                    authority);
            }
        }).ConfigureAwait(false);
        await sessionLoad.ConfigureAwait(false);
    }

    private Task StartSessionLoadOnPresentationThread(
        AgentSessionRecord? session,
        Guid? preferredMemoryId,
        LatestRequestTicket? authority)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _sessionLoadCancellation?.Cancel();
        var version = ++_sessionLoadVersion;
        if (session is null)
        {
            ClearSelectedSession();
            return Task.CompletedTask;
        }

        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetime.Token,
            authority?.CancellationToken ?? CancellationToken.None);
        _sessionLoadCancellation = cancellation;
        _selectedSessionSemanticContext = null;
        SemanticStatusText = "Loading semantic status...";
        CanReindexSemanticIndex = false;
        BeginBusy();
        return LoadSelectedSessionAsync(
            new MemorySessionLoadRequest(
                version,
                session.SessionId,
                preferredMemoryId,
                SearchText,
                IncludeInactive,
                _memorySelectionRevision,
                authority),
            cancellation);
    }

    private async Task LoadSelectedSessionAsync(
        MemorySessionLoadRequest request,
        CancellationTokenSource cancellation)
    {
        try
        {
            var semanticState = await _memoryInspectorService.GetSemanticSessionStateAsync(
                request.SessionId,
                cancellationToken: cancellation.Token).ConfigureAwait(false);
            var content = await CaptureSessionContentAsync(
                    request.SessionId,
                    request.PreferredMemoryId,
                    semanticState.Context,
                    request.SearchText,
                    request.IncludeInactive,
                    cancellation.Token)
                .ConfigureAwait(false);

            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentSessionLoad(request))
                {
                    return;
                }

                var memorySelectionChanged = request.MemorySelectionRevision != _memorySelectionRevision;
                var selectedMemoryId = memorySelectionChanged
                    ? SelectedMemory?.MemoryId
                    : content.SelectedMemoryId;
                if (memorySelectionChanged
                    && selectedMemoryId is not null
                    && !content.Memories.Any(memory => memory.MemoryId == selectedMemoryId))
                {
                    return;
                }

                _selectedSessionSemanticContext = semanticState.Context;
                SemanticStatusText = semanticState.Status.StatusText;
                CanReindexSemanticIndex = semanticState.Status.CanReindex;
                ApplySessionContent(memorySelectionChanged
                    ? content with
                    {
                        SelectedMemoryId = selectedMemoryId,
                        SelectionDetails = MemorySelectionDetails.Empty,
                    }
                    : content);
                if (memorySelectionChanged)
                {
                    StartMemoryDetailsLoad(SelectedMemory);
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (IsCurrentSessionLoad(request))
                {
                    _selectedSessionSemanticContext = SemanticEmbeddingContext.Unavailable($"Semantic status failed to load: {ex.Message}");
                    SemanticStatusText = ex.Message;
                    CanReindexSemanticIndex = false;
                    StatusText = ex.Message;
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    EndBusy();
                }

                CompleteSessionLoad(cancellation);
            }).ConfigureAwait(false);
        }
    }

    private void CompleteSessionLoad(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(_sessionLoadCancellation, cancellation))
        {
            _sessionLoadCancellation = null;
        }

        cancellation.Dispose();
    }

    private void ApplySelectionDetails(MemorySelectionDetails details)
    {
        EvidenceItems.Clear();
        SupersededMemoryItems.Clear();
        SupersedingMemory = null;
        CorrectionLineageSummaryText = string.Empty;
        if (details.Selection is null)
        {
            EditCategory = string.Empty;
            EditContent = string.Empty;
            return;
        }

        EditCategory = details.Selection.Category;
        EditContent = details.Selection.Content;
        foreach (var evidence in details.Evidence)
        {
            EvidenceItems.Add(evidence);
        }

        SupersedingMemory = details.SupersedingMemory;
        foreach (var supersededMemory in details.SupersededMemories)
        {
            SupersededMemoryItems.Add(supersededMemory);
        }

        CorrectionLineageSummaryText = details.LineageCount == 0
            ? string.Empty
            : $"This memory is part of a correction lineage with {details.LineageCount} related memory item(s).";

        OnPropertyChanged(nameof(HasSupersedingMemory));
        OnPropertyChanged(nameof(HasSupersededMemories));
        OnPropertyChanged(nameof(HasCorrectionLineage));
    }

    private void ClearSelectedSession()
    {
        _memoryDetailsLoadVersion++;
        _selectedSessionSemanticContext = null;
        WorkingSummaryText = string.Empty;
        EvidenceItems.Clear();
        SupersededMemoryItems.Clear();
        _suppressMemorySelectionDetails = true;
        try
        {
            Memories.Clear();
            SelectedMemory = null;
        }
        finally
        {
            _suppressMemorySelectionDetails = false;
        }
        SupersedingMemory = null;
        EditCategory = string.Empty;
        EditContent = string.Empty;
        CorrectionLineageSummaryText = string.Empty;
        SemanticStatusText = string.Empty;
        CanReindexSemanticIndex = false;
        StatusText = "No agent sessions are available yet.";
    }

    private void BeginBusy()
    {
        _busyOperationCount++;
        IsBusy = true;
    }

    private void EndBusy()
    {
        if (_busyOperationCount == 0)
        {
            return;
        }

        _busyOperationCount--;
        IsBusy = _busyOperationCount > 0;
    }

    private bool IsCurrentSessionLoad(MemorySessionLoadRequest request)
        => !_disposed
           && request.Version == _sessionLoadVersion
           && SelectedSession?.SessionId == request.SessionId
           && (request.Authority is not { } authority || _requests.IsCurrent(authority));

    private sealed record MemorySessionLoadRequest(
        int Version,
        Guid SessionId,
        Guid? PreferredMemoryId,
        string SearchText,
        bool IncludeInactive,
        long MemorySelectionRevision,
        LatestRequestTicket? Authority);

    private sealed record MemorySessionContent(
        string WorkingSummary,
        IReadOnlyList<MemoryListItemViewModel> Memories,
        Guid? SelectedMemoryId,
        MemorySelectionDetails SelectionDetails);

    private sealed record MemorySelectionDetails(
        MemoryListItemViewModel? Selection,
        IReadOnlyList<MemoryEvidenceItemViewModel> Evidence,
        MemoryListItemViewModel? SupersedingMemory,
        IReadOnlyList<MemoryListItemViewModel> SupersededMemories,
        int LineageCount)
    {
        public static MemorySelectionDetails Empty { get; } = new(null, [], null, [], 0);
    }
}

public sealed class MemoryListItemViewModel(StoredMemoryRecord record, MemorySemanticIndexStatusRecord semanticIndexStatus)
{
    public Guid MemoryId { get; } = record.MemoryId;

    public string Category { get; } = record.Category;

    public string CategoryLabel { get; } = FormatCategory(record.Category);

    public string Content { get; } = record.Content;

    public string PreviewText { get; } = record.Content.Length <= 140 ? record.Content : record.Content[..137].TrimEnd() + "...";

    public bool IsPinned { get; } = record.IsPinned;

    public string State { get; } = record.State;

    public string StateLabel { get; } = record.State;

    public Guid? SupersededByMemoryId { get; } = record.SupersededByMemoryId;

    public string SemanticIndexBadgeText { get; } = semanticIndexStatus.StatusLabel;

    public string SemanticIndexStatusText { get; } = semanticIndexStatus.StatusText;

    public bool HasSemanticIndexStatus => !string.IsNullOrWhiteSpace(SemanticIndexBadgeText);

    public string UpdatedAtLabel { get; } = record.UpdatedAtUtc.ToLocalTime().ToString("g");

    private static string FormatCategory(string category)
        => string.Join(' ', category.Split('-', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
}

public sealed class MemoryEvidenceItemViewModel(StoredMemoryEvidenceRecord record)
{
    public string Text { get; } = string.IsNullOrWhiteSpace(record.EvidenceText) ? "(No evidence text)" : record.EvidenceText;

    public string TimestampLabel { get; } = record.CreatedAtUtc.ToLocalTime().ToString("g");
}
