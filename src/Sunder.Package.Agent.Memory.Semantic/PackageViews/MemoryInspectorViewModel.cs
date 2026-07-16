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
    private readonly IMemoryInspectorGateway _memoryInspectorService;
    private readonly IPresentationDispatcher _uiDispatcher = PresentationDispatcher.Capture();
    private readonly PresentationTaskScope _tasks = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly AsyncOnce _initialization = new();
    private SemanticEmbeddingContext? _selectedSessionSemanticContext;
    private CancellationTokenSource? _sessionLoadCancellation;
    private bool _suppressSessionSelectionHandlers;
    private bool _suppressMemorySelectionDetails;
    private bool _isInitialized;
    private bool _disposed;
    private int _sessionLoadVersion;
    private int _busyOperationCount;

    internal MemoryInspectorViewModel(IMemoryInspectorGateway memoryInspectorService)
    {
        _memoryInspectorService = memoryInspectorService;
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

    partial void OnSelectedSessionChanged(AgentSessionRecord? value)
    {
        if (_suppressSessionSelectionHandlers)
        {
            return;
        }

        _ = StartSessionLoadAsync(value, preferredMemoryId: null);
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
        LoadSelectionDetails(value);
    }

    partial void OnSearchTextChanged(string value)
    {
        ReloadMemories();
    }

    partial void OnWorkingSummaryTextChanged(string value)
    {
        OnPropertyChanged(nameof(HasWorkingSummary));
    }

    partial void OnIncludeInactiveChanged(bool value)
    {
        ReloadMemories();
    }

    [RelayCommand]
    private void Refresh()
    {
        ReloadSessions();
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
    private void SaveMemory()
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
            _memoryInspectorService.UpdateMemory(SelectedMemory.MemoryId, category, content, "Corrected in memory inspector.");
            StatusText = "Memory saved.";
            ReloadMemories(SelectedMemory.MemoryId);
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    [RelayCommand]
    private void TogglePinned()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        _memoryInspectorService.SetPinned(SelectedMemory.MemoryId, !SelectedMemory.IsPinned);
        StatusText = SelectedMemory.IsPinned ? "Memory unpinned." : "Memory pinned.";
        ReloadMemories(SelectedMemory.MemoryId);
    }

    [RelayCommand]
    private void ContestMemory()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        _memoryInspectorService.ContestMemory(SelectedMemory.MemoryId);
        StatusText = "Memory marked as contested.";
        ReloadMemories(SelectedMemory.MemoryId);
    }

    [RelayCommand]
    private void ForgetMemory()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        _memoryInspectorService.ForgetMemory(SelectedMemory.MemoryId);
        StatusText = "Memory forgotten.";
        ReloadMemories();
    }

    [RelayCommand]
    private void SupersedeMemory()
    {
        if (SelectedMemory is null)
        {
            return;
        }

        _memoryInspectorService.SupersedeMemory(SelectedMemory.MemoryId);
        StatusText = "Memory superseded.";
        ReloadMemories();
    }

    [RelayCommand]
    private void CreateCorrection()
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
            var result = _memoryInspectorService.CreateCorrectedMemory(SelectedMemory.MemoryId, category, content);
            StatusText = result.CreatedNewMemory
                ? "Created corrected memory and linked the original as superseded."
                : "Applied correction to the existing memory.";
            ReloadMemories(result.CorrectedMemory.MemoryId);
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
        _lifetime.Cancel();
        _initialization.Dispose();
        _sessionLoadCancellation?.Cancel();
        _memoryInspectorService.SessionChanged -= OnSessionChanged;
        _memoryInspectorService.SemanticWorkerStatusChanged -= OnSemanticWorkerStatusChanged;
        _sessionLoadCancellation?.Dispose();
        _sessionLoadCancellation = null;
        _tasks.Dispose();
        _lifetime.Dispose();
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await _memoryInspectorService.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var initialState = await Task.Run(() => new MemoryInspectorInitialState(
            _memoryInspectorService.ListSessions(),
            _memoryInspectorService.GetSemanticWorkerStatus(),
            _memoryInspectorService.GetMetricsSnapshot()), cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        Task sessionLoad = Task.CompletedTask;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            ApplySessions(initialState.Sessions, preferredSessionId: null);
            ApplySemanticWorkerStatus(initialState.WorkerStatus);
            ApplyMetricsSummary(initialState.Metrics);
            _isInitialized = true;
            sessionLoad = StartSessionLoadAsync(SelectedSession, preferredMemoryId: null);
        }).ConfigureAwait(false);
        await sessionLoad.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private void ReloadSessions(Guid? preferredMemoryId = null)
    {
        var currentSelectedSessionId = SelectedSession?.SessionId;
        var sessions = _memoryInspectorService.ListSessions();

        ApplySessions(sessions, currentSelectedSessionId);

        if (SelectedSession is null)
        {
            _ = StartSessionLoadAsync(null, preferredMemoryId);
            return;
        }

        _ = StartSessionLoadAsync(SelectedSession, preferredMemoryId);
    }

    private void ApplySessions(
        IReadOnlyList<AgentSessionRecord> sessions,
        Guid? preferredSessionId)
    {

        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(session);
        }

        _suppressSessionSelectionHandlers = true;
        try
        {
            SelectedSession = Sessions.FirstOrDefault(session => session.SessionId == preferredSessionId)
                ?? Sessions.FirstOrDefault();
        }
        finally
        {
            _suppressSessionSelectionHandlers = false;
        }
    }

    private void ReloadMemories(Guid? preferredMemoryId = null)
    {
        if (SelectedSession is null)
        {
            Memories.Clear();
            EvidenceItems.Clear();
            SelectedMemory = null;
            WorkingSummaryText = string.Empty;
            return;
        }

        ApplySessionContent(CaptureSessionContent(
            SelectedSession.SessionId,
            preferredMemoryId,
            _selectedSessionSemanticContext));
    }

    private MemorySessionContent CaptureSessionContent(
        Guid sessionId,
        Guid? preferredMemoryId,
        SemanticEmbeddingContext? semanticContext)
    {
        var workingSummary = _memoryInspectorService.GetSessionContextCheckpoint(sessionId)?.SummaryText
            ?? _memoryInspectorService.GetWorkingSummary(sessionId)?.SummaryText
            ?? string.Empty;
        var memories = _memoryInspectorService.ListMemories(sessionId, SearchText, IncludeInactive)
            .Select(memory => new MemoryListItemViewModel(
                memory,
                _memoryInspectorService.GetSemanticIndexStatus(memory, semanticContext)))
            .ToArray();
        var selectedMemory = memories.FirstOrDefault(memory => memory.MemoryId == preferredMemoryId)
            ?? memories.FirstOrDefault();
        return new MemorySessionContent(
            workingSummary,
            memories,
            selectedMemory?.MemoryId,
            CaptureSelectionDetails(selectedMemory, semanticContext));
    }

    private void ApplySessionContent(MemorySessionContent content)
    {
        Memories.Clear();
        EvidenceItems.Clear();
        EditCategory = string.Empty;
        EditContent = string.Empty;
        WorkingSummaryText = content.WorkingSummary;

        _suppressMemorySelectionDetails = true;
        try
        {
            SelectedMemory = null;
        }
        finally
        {
            _suppressMemorySelectionDetails = false;
        }

        foreach (var memory in content.Memories)
        {
            Memories.Add(memory);
        }

        _suppressMemorySelectionDetails = true;
        try
        {
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

    private Task StartSessionLoadAsync(AgentSessionRecord? session, Guid? preferredMemoryId)
    {
        if (_disposed)
        {
            return Task.CompletedTask;
        }

        _sessionLoadCancellation?.Cancel();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _sessionLoadCancellation = cancellation;
        return LoadSelectedSessionAsync(session, preferredMemoryId, ++_sessionLoadVersion, cancellation);
    }

    private async Task LoadSelectedSessionAsync(
        AgentSessionRecord? session,
        Guid? preferredMemoryId,
        int version,
        CancellationTokenSource cancellation)
    {
        if (session is null)
        {
            ClearSelectedSession();
            CompleteSessionLoad(cancellation);
            return;
        }

        BeginBusy();
        try
        {
            _selectedSessionSemanticContext = null;
            SemanticStatusText = "Loading semantic status...";
            CanReindexSemanticIndex = false;

            var semanticState = await _memoryInspectorService.GetSemanticSessionStateAsync(
                session.SessionId,
                cancellationToken: cancellation.Token).ConfigureAwait(false);
            var content = await Task.Run(
                () => CaptureSessionContent(
                    session.SessionId,
                    preferredMemoryId,
                    semanticState.Context),
                cancellation.Token).ConfigureAwait(false);
            if (!IsCurrentSessionLoad(version, session.SessionId))
            {
                return;
            }

            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentSessionLoad(version, session.SessionId))
                {
                    return;
                }

                _selectedSessionSemanticContext = semanticState.Context;
                SemanticStatusText = semanticState.Status.StatusText;
                CanReindexSemanticIndex = semanticState.Status.CanReindex;
                ApplySessionContent(content);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (IsCurrentSessionLoad(version, session.SessionId))
            {
                await _uiDispatcher.InvokeAsync(() =>
                {
                    if (IsCurrentSessionLoad(version, session.SessionId))
                    {
                        _selectedSessionSemanticContext = SemanticEmbeddingContext.Unavailable($"Semantic status failed to load: {ex.Message}");
                        SemanticStatusText = ex.Message;
                        CanReindexSemanticIndex = false;
                        StatusText = ex.Message;
                    }
                }).ConfigureAwait(false);
            }
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

    private void RefreshSemanticWorkerStatus()
    {
        ApplySemanticWorkerStatus(_memoryInspectorService.GetSemanticWorkerStatus());
        RefreshMetricsSummary();
    }

    private void ApplySemanticWorkerStatus(SemanticMemoryWorkerStatusRecord status)
    {
        SemanticWorkerStatusText = status.StatusText;
        HasSemanticWorkerFailure = status.HasFailure;
        OnPropertyChanged(nameof(HasSemanticWorkerStatus));
    }

    private void RefreshMetricsSummary()
        => ApplyMetricsSummary(_memoryInspectorService.GetMetricsSnapshot());

    private void ApplyMetricsSummary(SemanticMemoryMetricsSnapshot metrics)
    {
        MemoryMetricsSummaryText =
            $"Promotions: {metrics.PromotionWriteCount}/{metrics.PromotionCandidateCount} candidates committed\n" +
            $"Recall: {metrics.RecallRequestCount} requests, {metrics.RecallEntryCount} total entries returned\n" +
            $"Corrections: {metrics.CorrectionCount}\n" +
            $"Worker failures: {metrics.WorkerFailureCount}";
        OnPropertyChanged(nameof(HasMemoryMetricsSummary));
    }

    private void LoadSelectionDetails(MemoryListItemViewModel? selection)
        => ApplySelectionDetails(CaptureSelectionDetails(selection, _selectedSessionSemanticContext));

    private MemorySelectionDetails CaptureSelectionDetails(
        MemoryListItemViewModel? selection,
        SemanticEmbeddingContext? semanticContext)
    {
        if (selection is null)
        {
            return MemorySelectionDetails.Empty;
        }

        var supersedingMemory = _memoryInspectorService.GetSupersedingMemory(selection.MemoryId);
        return new MemorySelectionDetails(
            selection,
            _memoryInspectorService.ListEvidence(selection.MemoryId)
                .Select(item => new MemoryEvidenceItemViewModel(item))
                .ToArray(),
            supersedingMemory is null
                ? null
                : new MemoryListItemViewModel(
                    supersedingMemory,
                    _memoryInspectorService.GetSemanticIndexStatus(supersedingMemory, semanticContext)),
            _memoryInspectorService.ListSupersededMemories(selection.MemoryId)
                .Select(item => new MemoryListItemViewModel(
                    item,
                    _memoryInspectorService.GetSemanticIndexStatus(item, semanticContext)))
                .ToArray(),
            _memoryInspectorService.ListCorrectionLineage(selection.MemoryId).Count);
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

    private void OnSessionChanged(Guid sessionId)
        => RunOnUiThread(() => ApplySessionChanged(sessionId));

    private void ApplySessionChanged(Guid sessionId)
    {
        if (!_disposed && _isInitialized && SelectedSession?.SessionId == sessionId)
        {
            ReloadSessions(SelectedMemory?.MemoryId);
        }
    }

    private void RunOnUiThread(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        _tasks.Run(_ => _uiDispatcher.InvokeAsync(action));
    }

    private void OnSemanticWorkerStatusChanged()
        => RunOnUiThread(() =>
        {
            if (!_disposed && _isInitialized)
            {
                RefreshSemanticWorkerStatus();
            }
        });

    private void ClearSelectedSession()
    {
        _selectedSessionSemanticContext = null;
        WorkingSummaryText = string.Empty;
        Memories.Clear();
        EvidenceItems.Clear();
        SupersededMemoryItems.Clear();
        SelectedMemory = null;
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

    private bool IsCurrentSessionLoad(int version, Guid sessionId)
        => !_disposed && version == _sessionLoadVersion && SelectedSession?.SessionId == sessionId;

    private sealed record MemoryInspectorInitialState(
        IReadOnlyList<AgentSessionRecord> Sessions,
        SemanticMemoryWorkerStatusRecord WorkerStatus,
        SemanticMemoryMetricsSnapshot Metrics);

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
