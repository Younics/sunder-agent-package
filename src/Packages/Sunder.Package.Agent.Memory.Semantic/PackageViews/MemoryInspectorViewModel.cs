using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed partial class MemoryInspectorViewModel : ObservableObject, IDisposable
{
    private readonly MemoryInspectorService _memoryInspectorService;
    private SemanticEmbeddingContext? _selectedSessionSemanticContext;
    private bool _suppressSessionSelectionHandlers;
    private int _sessionLoadVersion;
    private int _busyOperationCount;

    public MemoryInspectorViewModel(MemoryInspectorService memoryInspectorService)
    {
        _memoryInspectorService = memoryInspectorService;
        _memoryInspectorService.SessionChanged += OnSessionChanged;
        _memoryInspectorService.SemanticWorkerStatusChanged += OnSemanticWorkerStatusChanged;
        ReloadSessions();
        RefreshSemanticWorkerStatus();
        RefreshMetricsSummary();
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

    partial void OnSelectedSessionChanged(AgentSessionRecord? value)
    {
        if (_suppressSessionSelectionHandlers)
        {
            return;
        }

        _ = LoadSelectedSessionAsync(value, preferredMemoryId: null, ++_sessionLoadVersion);
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
            await LoadSelectedSessionAsync(SelectedSession, SelectedMemory?.MemoryId, ++_sessionLoadVersion);
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
        _memoryInspectorService.SessionChanged -= OnSessionChanged;
        _memoryInspectorService.SemanticWorkerStatusChanged -= OnSemanticWorkerStatusChanged;
    }

    private void ReloadSessions(Guid? preferredMemoryId = null)
    {
        var currentSelectedSessionId = SelectedSession?.SessionId;
        var sessions = _memoryInspectorService.ListSessions();

        Sessions.Clear();
        foreach (var session in sessions)
        {
            Sessions.Add(session);
        }

        _suppressSessionSelectionHandlers = true;
        try
        {
            SelectedSession = Sessions.FirstOrDefault(session => session.SessionId == currentSelectedSessionId)
                ?? Sessions.FirstOrDefault();
        }
        finally
        {
            _suppressSessionSelectionHandlers = false;
        }

        if (SelectedSession is null)
        {
            ClearSelectedSession();
            return;
        }

        _ = LoadSelectedSessionAsync(SelectedSession, preferredMemoryId, ++_sessionLoadVersion);
    }

    private void ReloadMemories(Guid? preferredMemoryId = null)
    {
        Memories.Clear();
        EvidenceItems.Clear();
        SelectedMemory = null;
        EditCategory = string.Empty;
        EditContent = string.Empty;

        if (SelectedSession is null)
        {
            WorkingSummaryText = string.Empty;
            return;
        }

        WorkingSummaryText = _memoryInspectorService.GetWorkingSummary(SelectedSession.SessionId)?.SummaryText ?? string.Empty;

        var memories = _memoryInspectorService.ListMemories(SelectedSession.SessionId, SearchText, IncludeInactive)
            .Select(memory => new MemoryListItemViewModel(memory, _memoryInspectorService.GetSemanticIndexStatus(memory, _selectedSessionSemanticContext)))
            .ToArray();
        foreach (var memory in memories)
        {
            Memories.Add(memory);
        }

        SelectedMemory = Memories.FirstOrDefault(memory => memory.MemoryId == preferredMemoryId)
            ?? Memories.FirstOrDefault();

        if (Memories.Count == 0)
        {
            StatusText = "No stored memories match the current filter.";
        }
    }

    private async Task LoadSelectedSessionAsync(AgentSessionRecord? session, Guid? preferredMemoryId, int version)
    {
        if (session is null)
        {
            ClearSelectedSession();
            return;
        }

        BeginBusy();
        try
        {
            _selectedSessionSemanticContext = null;
            SemanticStatusText = "Loading semantic status...";
            CanReindexSemanticIndex = false;
            ReloadMemories(preferredMemoryId);

            var semanticState = await _memoryInspectorService.GetSemanticSessionStateAsync(session.SessionId);
            if (!IsCurrentSessionLoad(version, session.SessionId))
            {
                return;
            }

            _selectedSessionSemanticContext = semanticState.Context;
            SemanticStatusText = semanticState.Status.StatusText;
            CanReindexSemanticIndex = semanticState.Status.CanReindex;
            ReloadMemories(preferredMemoryId);
        }
        catch (Exception ex)
        {
            if (IsCurrentSessionLoad(version, session.SessionId))
            {
                _selectedSessionSemanticContext = SemanticEmbeddingContext.Unavailable($"Semantic status failed to load: {ex.Message}");
                SemanticStatusText = ex.Message;
                CanReindexSemanticIndex = false;
                StatusText = ex.Message;
                ReloadMemories(preferredMemoryId);
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private void RefreshSemanticWorkerStatus()
    {
        var status = _memoryInspectorService.GetSemanticWorkerStatus();
        SemanticWorkerStatusText = status.StatusText;
        HasSemanticWorkerFailure = status.HasFailure;
        OnPropertyChanged(nameof(HasSemanticWorkerStatus));
        RefreshMetricsSummary();
    }

    private void RefreshMetricsSummary()
    {
        var metrics = _memoryInspectorService.GetMetricsSnapshot();
        MemoryMetricsSummaryText =
            $"Promotions: {metrics.PromotionWriteCount}/{metrics.PromotionCandidateCount} candidates committed\n" +
            $"Recall: {metrics.RecallRequestCount} requests, {metrics.RecallEntryCount} total entries returned\n" +
            $"Corrections: {metrics.CorrectionCount}\n" +
            $"Worker failures: {metrics.WorkerFailureCount}";
        OnPropertyChanged(nameof(HasMemoryMetricsSummary));
    }

    private void LoadSelectionDetails(MemoryListItemViewModel? selection)
    {
        EvidenceItems.Clear();
        SupersededMemoryItems.Clear();
        SupersedingMemory = null;
        CorrectionLineageSummaryText = string.Empty;
        if (selection is null)
        {
            EditCategory = string.Empty;
            EditContent = string.Empty;
            return;
        }

        EditCategory = selection.Category;
        EditContent = selection.Content;
        foreach (var evidence in _memoryInspectorService.ListEvidence(selection.MemoryId)
                     .Select(item => new MemoryEvidenceItemViewModel(item)))
        {
            EvidenceItems.Add(evidence);
        }

        SupersedingMemory = _memoryInspectorService.GetSupersedingMemory(selection.MemoryId) is { } supersedingMemory
            ? new MemoryListItemViewModel(supersedingMemory, _memoryInspectorService.GetSemanticIndexStatus(supersedingMemory, _selectedSessionSemanticContext))
            : null;
        foreach (var supersededMemory in _memoryInspectorService.ListSupersededMemories(selection.MemoryId)
                     .Select(item => new MemoryListItemViewModel(item, _memoryInspectorService.GetSemanticIndexStatus(item, _selectedSessionSemanticContext))))
        {
            SupersededMemoryItems.Add(supersededMemory);
        }

        var lineageCount = _memoryInspectorService.ListCorrectionLineage(selection.MemoryId).Count;
        CorrectionLineageSummaryText = lineageCount == 0
            ? string.Empty
            : $"This memory is part of a correction lineage with {lineageCount} related memory item(s).";

        OnPropertyChanged(nameof(HasSupersedingMemory));
        OnPropertyChanged(nameof(HasSupersededMemories));
        OnPropertyChanged(nameof(HasCorrectionLineage));
    }

    private void OnSessionChanged(Guid sessionId)
        => RunOnUiThread(() => ApplySessionChanged(sessionId));

    private void ApplySessionChanged(Guid sessionId)
    {
        if (SelectedSession?.SessionId == sessionId)
        {
            ReloadSessions(SelectedMemory?.MemoryId);
        }
    }

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private void OnSemanticWorkerStatusChanged()
        => Dispatcher.UIThread.Post(RefreshSemanticWorkerStatus, DispatcherPriority.Background);

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
        => version == _sessionLoadVersion && SelectedSession?.SessionId == sessionId;
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
