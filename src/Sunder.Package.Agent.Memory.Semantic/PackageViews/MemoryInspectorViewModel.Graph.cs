using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed partial class MemoryInspectorViewModel
{
    private int _pageLimit = 200;
    private readonly Dictionary<Guid, (string Category, string Content)> _memoryDrafts = [];
    private Guid? _draftMemoryId;
    private string _baselineCategory = string.Empty;
    private string _baselineContent = string.Empty;
    private Guid? _contentSessionId;
    private MemoryListItemViewModel? _expandedSelection;
    private IReadOnlyDictionary<Guid, MemoryListItemViewModel> _graphMemories = new Dictionary<Guid, MemoryListItemViewModel>();

    internal MemoryGraphState Graph { get; private set; } = new();
    [ObservableProperty] private int _selectedTab;
    [ObservableProperty] private string? _categoryFilter;
    [ObservableProperty] private bool _onlyPinned;
    [ObservableProperty] private bool _hasMoreMemories;
    public bool CanLoadMoreMemories => HasMoreMemories && _pageLimit < 1000 && Memories.Count >= _pageLimit;
    public bool HasBoundedMemoryPage => HasMoreMemories && !CanLoadMoreMemories;
    public bool IsMemoryDirty => _draftMemoryId is not null && (EditCategory != _baselineCategory || EditContent != _baselineContent);
    public ObservableCollection<MemoryListItemViewModel> FilteredMemories { get; } = [];
    public IEnumerable<string> CategoryFilters => new[] { "All categories" }.Concat(CategoryOptions);
    public Guid? SelectedGraphMemoryId
    {
        get => SelectedMemory?.MemoryId;
        set
        {
            if (value is null) return;
            if (!_graphMemories.TryGetValue(value.Value, out var memory)) return;
            _expandedSelection = Memories.Any(item => item.MemoryId == value) ? null : memory;
            RefreshFilteredMemories();
            SelectedMemory = memory;
        }
    }

    partial void OnCategoryFilterChanged(string? value) => RebuildGraph();
    partial void OnOnlyPinnedChanged(bool value) => RebuildGraph();
    partial void OnHasMoreMemoriesChanged(bool value) => NotifyMemoryPage();
    partial void OnEditCategoryChanged(string value) => OnPropertyChanged(nameof(IsMemoryDirty));
    partial void OnEditContentChanged(string value) => OnPropertyChanged(nameof(IsMemoryDirty));

    [RelayCommand]
    private Task LoadMoreMemoriesAsync()
        => _operations.RunAsync(
            MemoryRefreshOperation,
            LoadMoreMemoriesCoreAsync,
            ReconcileMemoryAsync);

    private async Task LoadMoreMemoriesCoreAsync(CancellationToken cancellationToken)
    {
        _pageLimit = Math.Min(_pageLimit + 200, 1000);
        await StartSessionLoadAsync(SelectedSession, SelectedMemory?.MemoryId).WaitAsync(cancellationToken);
        NotifyMemoryPage();
    }

    private void RememberMemoryDraft()
    {
        if (_draftMemoryId is not { } id) return;
        if (IsMemoryDirty) _memoryDrafts[id] = (EditCategory, EditContent);
        else _memoryDrafts.Remove(id);
    }

    private void ApplyMemoryDraft(MemoryListItemViewModel? memory)
    {
        RememberMemoryDraft();
        _draftMemoryId = memory?.MemoryId;
        _baselineCategory = memory?.Category ?? string.Empty;
        _baselineContent = memory?.Content ?? string.Empty;
        var draft = memory is not null && _memoryDrafts.TryGetValue(memory.MemoryId, out var saved) ? saved : (_baselineCategory, _baselineContent);
        EditCategory = draft.Item1;
        EditContent = draft.Item2;
        OnPropertyChanged(nameof(IsMemoryDirty));
    }

    private void AcceptSubmittedMemory(Guid id, string category, string content, (string Category, string Content) submitted)
    {
        if (_draftMemoryId == id)
        {
            if ((EditCategory, EditContent) == submitted)
            {
                EditCategory = category;
                EditContent = content;
            }
            _baselineCategory = category;
            _baselineContent = content;
            RememberMemoryDraft();
        }
        else if (_memoryDrafts.TryGetValue(id, out var draft) && draft == submitted) _memoryDrafts.Remove(id);
    }

    private void RebuildGraph()
    {
        RefreshFilteredMemories();
        var graph = new MemoryGraphState();
        var items = FilteredMemories.ToList();
        if (SelectedMemory is { } selected && items.Any(memory => memory.MemoryId == selected.MemoryId))
        {
            if (SupersedingMemory is { } superseding && MatchesMemoryFilter(superseding)) items.Add(superseding);
            items.AddRange(SupersededMemoryItems.Where(MatchesMemoryFilter));
        }
        _graphMemories = items.DistinctBy(memory => memory.MemoryId).ToDictionary(memory => memory.MemoryId);
        graph.Apply(_graphMemories.Values.Select(memory => memory.Record), EvidenceItems.Select(evidence => evidence.Record), SelectedSession?.SessionId);
        Graph = graph;
        OnPropertyChanged(nameof(Graph));
        OnPropertyChanged(nameof(SelectedGraphMemoryId));
    }

    private void RefreshFilteredMemories()
    {
        IEnumerable<MemoryListItemViewModel> items = Memories;
        if (_expandedSelection is { } expanded && expanded.Record.SessionId == SelectedSession?.SessionId)
            items = items.Append(expanded);
        ObservableCollectionReconciler.Apply(FilteredMemories, items.DistinctBy(memory => memory.MemoryId).Where(MatchesMemoryFilter));
    }

    private bool MatchesMemoryFilter(MemoryListItemViewModel memory)
        => (string.IsNullOrEmpty(CategoryFilter) || CategoryFilter == "All categories" || memory.Category == CategoryFilter)
           && (!OnlyPinned || memory.IsPinned);

    private (long Session, long Memory) CaptureMemoryAction() => (_sessionSelectionRevision, _memorySelectionRevision);

    private Task RefreshAfterMemoryActionAsync((long Session, long Memory) selection, Guid? preferredMemoryId)
        => !_disposed && selection.Session == _sessionSelectionRevision
            ? StartSessionLoadAsync(SelectedSession, selection.Memory == _memorySelectionRevision ? preferredMemoryId : SelectedMemory?.MemoryId)
            : Task.CompletedTask;

    private void NotifyMemoryPage()
    {
        OnPropertyChanged(nameof(CanLoadMoreMemories));
        OnPropertyChanged(nameof(HasBoundedMemoryPage));
    }
}
