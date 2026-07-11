using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubagentsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);
    private static readonly SubagentEditorDraftComparer DraftComparer = new();
    private readonly SubagentService? _subagentService;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly SubagentEditorCapabilityCatalog? _capabilityCatalog;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly TimedStatusController _statusClear;
    private readonly Task _initialization;
    private readonly Dictionary<string, EditableDocumentState<SubagentEditorDraft>> _drafts =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSelectionHandlers;
    private bool _suppressSubagentChangeNotifications;
    private bool _suppressDraftTracking;
    private bool _isHydrating;
    private bool _disposed;
    private int _loadVersion;
    private long _editRevision;

    public SubagentsViewModel(
        SubagentService subagentService,
        IPackageExtensionCatalog extensionCatalog,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(
            subagentService,
            extensionCatalog,
            settingsNavigationService,
            PresentationDispatcher.Capture())
    {
    }

    internal SubagentsViewModel(
        SubagentService subagentService,
        IPackageExtensionCatalog extensionCatalog,
        IPackageSettingsNavigationService? settingsNavigationService,
        IPresentationDispatcher uiDispatcher)
    {
        _subagentService = subagentService;
        _settingsNavigationService = settingsNavigationService;
        _capabilityCatalog = new SubagentEditorCapabilityCatalog(extensionCatalog);
        _uiDispatcher = uiDispatcher;
        _statusClear = new TimedStatusController(dispatcher: uiDispatcher);
        ChatBinding = SubagentModelBindingEditor.Create(
            ProviderModelCatalogAdapter.ForChatProviders(extensionCatalog),
            uiDispatcher);
        Subscribe();
        _initialization = InitializeCoreAsync();
    }

    public SubagentsViewModel()
    {
        _uiDispatcher = PresentationDispatcher.Capture();
        _statusClear = new TimedStatusController(dispatcher: _uiDispatcher);
        ChatBinding = SubagentModelBindingEditor.Create(new ProviderModelCatalogAdapter(
            () => [],
            (_, _) => Task.FromResult(new ProviderModelCatalogResult([], string.Empty))),
            _uiDispatcher);
        Subscribe();
        _initialization = InitializeCoreAsync();
    }

    public ObservableCollection<SubagentRecord> Subagents { get; } = [];

    internal ModelBindingEditorState ChatBinding { get; }

    internal CapabilitySelectionState Capabilities { get; } = new();

    internal ObservableCollection<ProviderCatalogOption> ChatProviders => ChatBinding.Providers;

    internal ObservableCollection<ProviderModelCatalogOption> ChatModels => ChatBinding.Models;

    internal ObservableCollection<ModelReasoningOption> ReasoningOptions => ChatBinding.ReasoningOptions;

    internal ObservableCollection<ModelSpeedOption> SpeedOptions => ChatBinding.SpeedOptions;

    internal ObservableCollection<ModelModeOption> ModeOptions => ChatBinding.ModeOptions;

    internal ObservableCollection<CapabilityOptionState> CapabilityOptions => Capabilities.Options;

    internal ObservableCollection<CapabilityGroupState> CapabilityGroups => Capabilities.Groups;

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    public bool IsDirty => SelectedSubagent is not null
        && _drafts.TryGetValue(SelectedSubagent.SubagentId, out var draft)
        && draft.IsDirty;

    public bool IsHydrating => _isHydrating;

    public bool IsBusy => IsHydrating || ChatBinding.IsLoading;

    public bool IsEditorEnabled => HasSelectedSubagent && !IsHydrating;

    public bool CanNavigateSubagents => !IsHydrating;

    [ObservableProperty]
    private SubagentRecord? _selectedSubagent;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isEditorActive;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private SubagentStatusKind _statusKind = SubagentStatusKind.None;

    internal ProviderCatalogOption? SelectedChatProvider
    {
        get => ChatBinding.SelectedProvider;
        set => ChatBinding.SelectedProvider = value;
    }

    internal ProviderModelCatalogOption? SelectedChatModel
    {
        get => ChatBinding.SelectedModel;
        set => ChatBinding.SelectedModel = value;
    }

    internal ModelReasoningOption? SelectedReasoningOption
    {
        get => ChatBinding.SelectedReasoningOption;
        set => ChatBinding.SelectedReasoningOption = value;
    }

    internal ModelSpeedOption? SelectedSpeedOption
    {
        get => ChatBinding.SelectedSpeedOption;
        set => ChatBinding.SelectedSpeedOption = value;
    }

    internal ModelModeOption? SelectedModeOption
    {
        get => ChatBinding.SelectedModeOption;
        set => ChatBinding.SelectedModeOption = value;
    }

    public bool HasSelectedSubagent => SelectedSubagent is not null;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsStatusSuccess => StatusKind == SubagentStatusKind.Success;

    public bool IsStatusWarning => StatusKind == SubagentStatusKind.Warning;

    public bool IsStatusError => StatusKind == SubagentStatusKind.Error;

    public bool CanSaveSelectedSubagent => SelectedSubagent is not null
        && !string.IsNullOrWhiteSpace(Description)
        && !IsBusy;

    public bool IsSelectedSubagentIncomplete => SelectedSubagent is not null
        && string.IsNullOrWhiteSpace(Description);

    public string DescriptionValidationText => IsSelectedSubagentIncomplete
        ? "Description is required before this subagent can be saved, selected, or used for delegation."
        : string.Empty;

    public bool HasSelectedChatProvider => ChatBinding.HasSelectedProvider;

    public bool HasReasoningOptions => ChatBinding.HasReasoningOptions;

    public bool HasSpeedOptions => ChatBinding.HasSpeedOptions;

    public bool HasModeOptions => ChatBinding.HasModeOptions;

    public bool HasChatProviderChoices => ChatBinding.HasProviders;

    public bool HasNoChatProviderChoices => ChatBinding.HasNoProviders;

    public bool HasChatProviderWarning => ChatBinding.HasWarning;

    public string ChatProviderWarningText => ChatBinding.WarningText;

    public bool ShowChatProviderPicker => ChatBinding.ShowProviderPicker;

    public bool ShowChatProviderWarning => ChatBinding.ShowProviderWarning;

    public bool ShowChatModelSelection => ChatBinding.ShowModelSelection;

    public bool ShowReasoningOptions => ChatBinding.ShowReasoningOptions;

    public bool ShowSpeedOptions => ChatBinding.ShowSpeedOptions;

    public bool ShowModeOptions => ChatBinding.ShowModeOptions;

    public bool IsReasoningSelectionEnabled => ChatBinding.IsReasoningSelectionEnabled;

    public bool CanOpenChatProviderSettings => ChatBinding.CanOpenProviderSettings
        && _settingsNavigationService is not null;

    partial void OnSelectedSubagentChanging(SubagentRecord? value)
    {
        if (!IsHydrating)
        {
            UpdateCurrentDraft();
        }
    }

    partial void OnSelectedSubagentChanged(SubagentRecord? value)
    {
        OnPropertyChanged(nameof(HasSelectedSubagent));
        OnPropertyChanged(nameof(IsEditorEnabled));
        NotifyDescriptionStateChanged();
        SaveSubagentCommand.NotifyCanExecuteChanged();
        DeleteSubagentCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsDirty));
        if (_suppressSelectionHandlers)
        {
            return;
        }

        _ = LoadSelectedSubagentAsync(value, ++_loadVersion);
        if (IsCompactLayout && value is not null)
        {
            IsEditorActive = true;
        }
    }

    partial void OnDisplayNameChanged(string value) => OnEditorChanged();

    partial void OnDescriptionChanged(string value)
    {
        NotifyDescriptionStateChanged();
        OnEditorChanged();
    }

    partial void OnInstructionsChanged(string value) => OnEditorChanged();

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsEditorActive)
        {
            SelectedSubagent = null;
        }
        else if (!value && SelectedSubagent is null)
        {
            SelectedSubagent = Subagents.FirstOrDefault();
        }

        NotifyLayoutChanged();
    }

    partial void OnIsEditorActiveChanged(bool value) => NotifyLayoutChanged();

    [RelayCommand(CanExecute = nameof(CanNavigateSubagents))]
    private async Task CreateSubagentAsync()
    {
        if (_subagentService is null)
        {
            return;
        }

        SubagentRecord created;
        _suppressSubagentChangeNotifications = true;
        try
        {
            created = _subagentService.CreateSubagent("New Subagent");
        }
        finally
        {
            _suppressSubagentChangeNotifications = false;
        }

        await ReloadAsync(created.SubagentId);
        IsEditorActive = true;
        ClearStatus();
    }

    [RelayCommand(CanExecute = nameof(CanSaveSubagent))]
    private async Task SaveSubagentAsync()
    {
        if (_subagentService is null || SelectedSubagent is null || !CanSaveSubagent())
        {
            return;
        }

        try
        {
            SubagentRecord saved;
            _suppressSubagentChangeNotifications = true;
            try
            {
                saved = _subagentService.SaveSubagent(
                    SelectedSubagent.SubagentId,
                    DisplayName,
                    Description,
                    Instructions,
                    ChatBinding.SelectedProvider?.Id,
                    ChatBinding.SelectedModel?.Id,
                    Capabilities.Assignments,
                    ChatBinding.SettingsJson);
            }
            finally
            {
                _suppressSubagentChangeNotifications = false;
            }

            _drafts.Remove(saved.SubagentId);
            OnPropertyChanged(nameof(IsDirty));
            var shouldClearSelection = IsCompactLayout;
            await ReloadAsync(saved.SubagentId);
            if (shouldClearSelection)
            {
                SelectedSubagent = null;
                ClearStatus();
            }
            else
            {
                SetStatus("Subagent saved.", SubagentStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
        }
        catch (InvalidOperationException ex)
        {
            SetStatus(ex.Message, SubagentStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditSubagent))]
    private async Task DeleteSubagentAsync()
    {
        if (_subagentService is null || SelectedSubagent is null)
        {
            return;
        }

        var subagentId = SelectedSubagent.SubagentId;
        var deletedName = SelectedSubagent.DisplayName;
        var shouldClearSelection = IsCompactLayout;
        _suppressSubagentChangeNotifications = true;
        try
        {
            _subagentService.DeleteSubagent(subagentId);
        }
        finally
        {
            _suppressSubagentChangeNotifications = false;
        }

        _drafts.Remove(subagentId);
        await ReloadAsync(null);
        if (shouldClearSelection)
        {
            SelectedSubagent = null;
            ClearStatus();
        }
        else
        {
            SetStatus($"Deleted subagent '{deletedName}'.", SubagentStatusKind.Success, autoClear: true);
        }

        IsEditorActive = false;
    }

    private bool CanEditSubagent() => SelectedSubagent is not null && !IsBusy;

    private bool CanSaveSubagent() => CanSaveSelectedSubagent;

    [RelayCommand(CanExecute = nameof(CanNavigateSubagents))]
    private void BackToSubagentList()
    {
        UpdateCurrentDraft();
        if (IsCompactLayout)
        {
            SelectedSubagent = null;
        }

        IsEditorActive = false;
    }

    [RelayCommand]
    private async Task ReloadSubagentChatProvidersAsync()
    {
        if (SelectedSubagent is null)
        {
            return;
        }

        try
        {
            await ChatBinding.RefreshAsync(ChatBinding.Selection);
            UpdateCurrentDraft();
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SubagentStatusKind.Error);
        }
    }

    [RelayCommand]
    private Task OpenSelectedChatProviderSettingsAsync()
        => OpenProviderSettingsAsync(ChatBinding.SelectedProvider?.PackageId);

    [RelayCommand]
    private void OpenSubagentEditor(SubagentRecord? subagent)
    {
        if (subagent is not null)
        {
            ActivateSubagent(subagent);
        }
    }

    public void ActivateSubagent(SubagentRecord subagent)
    {
        if (!CanNavigateSubagents)
        {
            return;
        }

        if (!string.Equals(
            SelectedSubagent?.SubagentId,
            subagent.SubagentId,
            StringComparison.OrdinalIgnoreCase))
        {
            SelectedSubagent = subagent;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    private async Task ReloadAsync(string? selectedSubagentId)
    {
        if (_subagentService is null)
        {
            ClearEditor();
            return;
        }

        var currentSubagentId = SelectedSubagent?.SubagentId;
        SetSelectionSilently(() =>
        {
            Subagents.Clear();
            foreach (var subagent in _subagentService.ListSubagents())
            {
                Subagents.Add(subagent);
            }

            var selected = Subagents.FirstOrDefault(subagent => string.Equals(
                subagent.SubagentId,
                selectedSubagentId,
                StringComparison.OrdinalIgnoreCase));
            if (selected is null && (!IsCompactLayout || selectedSubagentId is not null))
            {
                selected = Subagents.FirstOrDefault(subagent => string.Equals(
                        subagent.SubagentId,
                        currentSubagentId,
                        StringComparison.OrdinalIgnoreCase))
                    ?? Subagents.FirstOrDefault();
            }

            SelectedSubagent = selected;
        });

        if (SelectedSubagent is null)
        {
            ClearEditor();
            return;
        }

        await LoadSelectedSubagentAsync(SelectedSubagent, ++_loadVersion);
    }

    private async Task LoadSelectedSubagentAsync(SubagentRecord? subagent, int version)
    {
        if (subagent is null)
        {
            ClearEditor();
            return;
        }

        BeginHydration(version);
        var startEditRevision = _editRevision;
        try
        {
            var hasDraft = _drafts.TryGetValue(subagent.SubagentId, out var document);
            var preserveDirtyDraft = hasDraft && document!.IsDirty;
            var draft = preserveDirtyDraft
                ? document!.Value
                : CreatePersistedDraft(subagent);
            if (!preserveDirtyDraft)
            {
                document = new EditableDocumentState<SubagentEditorDraft>(draft, DraftComparer);
                _drafts[subagent.SubagentId] = document;
            }

            var localToolsTask = _capabilityCatalog?.ListLocalToolsAsync()
                ?? Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);
            var packageCapabilitiesTask = _capabilityCatalog?.ListPackageCapabilitiesAsync()
                ?? Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);

            _suppressDraftTracking = true;
            try
            {
                DisplayName = draft.DisplayName;
                Description = draft.Description;
                Instructions = draft.Instructions;
            }
            finally
            {
                _suppressDraftTracking = false;
            }

            await Task.WhenAll(
                ChatBinding.RefreshAsync(draft.ChatBinding),
                localToolsTask,
                packageCapabilitiesTask).ConfigureAwait(false);
            var localTools = await localToolsTask.ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentLoad(version, subagent.SubagentId))
                {
                    return;
                }

                _suppressDraftTracking = true;
                try
                {
                    ApplyCapabilityOptions(
                        localTools,
                        packageCapabilities,
                        draft.CapabilityAssignments,
                        preserveCurrent: false);
                }
                finally
                {
                    _suppressDraftTracking = false;
                }

                var current = CaptureDraft();
                if (!preserveDirtyDraft && _editRevision == startEditRevision)
                {
                    _drafts[subagent.SubagentId] = new EditableDocumentState<SubagentEditorDraft>(
                        current,
                        DraftComparer);
                }
                else
                {
                    document!.Value = current;
                }

                OnPropertyChanged(nameof(IsDirty));
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed && IsCurrentLoad(version, subagent.SubagentId))
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() => EndHydration(version)).ConfigureAwait(false);
        }
    }

    private async Task RefreshSelectedSubagentCapabilitiesAsync()
    {
        var subagent = SelectedSubagent;
        if (subagent is null)
        {
            return;
        }

        var version = _loadVersion;
        try
        {
            var localToolsTask = _capabilityCatalog?.ListLocalToolsAsync()
                ?? Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);
            var packageCapabilitiesTask = _capabilityCatalog?.ListPackageCapabilitiesAsync()
                ?? Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
            await Task.WhenAll(localToolsTask, packageCapabilitiesTask).ConfigureAwait(false);
            var localTools = await localToolsTask.ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentLoad(version, subagent.SubagentId))
                {
                    return;
                }

                _suppressDraftTracking = true;
                try
                {
                    ApplyCapabilityOptions(
                        localTools,
                        packageCapabilities,
                        Capabilities.Assignments,
                        preserveCurrent: true);
                }
                finally
                {
                    _suppressDraftTracking = false;
                }

                UpdateCurrentDraft();
            }).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed && IsCurrentLoad(version, subagent.SubagentId))
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
    }

    private void ApplyCapabilityOptions(
        IReadOnlyList<AgentToolDescriptor> localTools,
        IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> packageCapabilities,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> assignments,
        bool preserveCurrent)
    {
        var definitions = localTools.Select(descriptor =>
            {
                var aliases = descriptor.Aliases?
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        alias,
                        descriptor.SourceId))
                    .ToArray();
                return new CapabilityOptionDefinition(
                    "capability",
                    AgentProfileSelectableCapabilityKinds.Tool,
                    descriptor.ToolId,
                    descriptor.SourceId,
                    descriptor.DisplayName,
                    descriptor.Description,
                    string.Empty,
                    CanSelect: true,
                    CapabilityGrouping.ForTool(descriptor),
                    aliases,
                    AllowUnscopedAssignment: true);
            })
            .Concat(packageCapabilities
                .Where(capability => !string.Equals(
                        capability.Kind,
                        AgentProfileSelectableCapabilityKinds.Subagent,
                        StringComparison.OrdinalIgnoreCase)
                    || !string.Equals(
                        capability.SourceId,
                        SubagentConstants.PackageId,
                        StringComparison.OrdinalIgnoreCase))
                .Select(capability => new CapabilityOptionDefinition(
                    "capability",
                    capability.Kind,
                    capability.CapabilityId,
                    capability.SourceId,
                    capability.DisplayName,
                    capability.Description,
                    capability.StatusText ?? string.Empty,
                    capability.IsSelectable,
                    CapabilityGrouping.ForPackage(capability))))
            .ToArray();
        if (preserveCurrent)
        {
            Capabilities.Reconcile(definitions);
        }
        else
        {
            Capabilities.Load(definitions, assignments);
        }
    }

    private SubagentEditorDraft CreatePersistedDraft(SubagentRecord subagent) => new(
        subagent.DisplayName,
        subagent.Description ?? string.Empty,
        subagent.Instructions ?? string.Empty,
        new ModelBindingSelection(
            subagent.ChatProviderId,
            subagent.ChatModelId,
            subagent.ChatModelSettingsJson),
        subagent.SelectableCapabilityAssignments ?? []);

    private SubagentEditorDraft CaptureDraft() => new(
        DisplayName,
        Description,
        Instructions,
        ChatBinding.Selection,
        Capabilities.Assignments);

    private void UpdateCurrentDraft()
    {
        if (_suppressDraftTracking || SelectedSubagent is null
            || !_drafts.TryGetValue(SelectedSubagent.SubagentId, out var document))
        {
            return;
        }

        var wasDirty = document.IsDirty;
        document.Value = CaptureDraft();
        if (wasDirty != document.IsDirty)
        {
            OnPropertyChanged(nameof(IsDirty));
        }
    }

    private void OnEditorChanged()
    {
        if (_suppressDraftTracking)
        {
            return;
        }

        _editRevision++;
        UpdateCurrentDraft();
    }

    private void Subscribe()
    {
        ChatBinding.PropertyChanged += OnModelBindingPropertyChanged;
        ChatBinding.Changed += OnEditorSelectionChanged;
        Capabilities.Changed += OnCapabilitiesChanged;
        if (_subagentService is not null)
        {
            _subagentService.SubagentsChanged += OnSubagentsChanged;
        }

        if (_capabilityCatalog is not null)
        {
            _capabilityCatalog.Changed += OnSelectableCapabilitiesChanged;
        }
    }

    private void OnModelBindingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        // The composed state owns the dependency graph; refresh the thin compatibility surface.
        OnPropertyChanged(string.Empty);
        SaveSubagentCommand.NotifyCanExecuteChanged();
        DeleteSubagentCommand.NotifyCanExecuteChanged();
    }

    private void OnEditorSelectionChanged() => OnEditorChanged();

    private void OnCapabilitiesChanged() => OnEditorChanged();

    private void OnSelectableCapabilitiesChanged()
        => RunOnUiThread(() => _ = RefreshSelectedSubagentCapabilitiesAsync());

    private void OnSubagentsChanged() => RunOnUiThread(() =>
    {
        if (!_suppressSubagentChangeNotifications)
        {
            _ = ReloadSafelyAsync(SelectedSubagent?.SubagentId);
        }
    });

    private async Task ReloadSafelyAsync(string? selectedSubagentId)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await ReloadAsync(selectedSubagentId);
        }
        catch (Exception ex)
        {
            ClearEditor();
            SetStatus(ex.Message, SubagentStatusKind.Error);
        }
    }

    private void ClearEditor()
    {
        _suppressDraftTracking = true;
        try
        {
            DisplayName = string.Empty;
            Description = string.Empty;
            Instructions = string.Empty;
            ChatBinding.Clear();
            Capabilities.Clear();
        }
        finally
        {
            _suppressDraftTracking = false;
        }

        OnPropertyChanged(nameof(IsDirty));
    }

    private void NotifyDescriptionStateChanged()
    {
        OnPropertyChanged(nameof(CanSaveSelectedSubagent));
        OnPropertyChanged(nameof(IsSelectedSubagentIncomplete));
        OnPropertyChanged(nameof(DescriptionValidationText));
        SaveSubagentCommand.NotifyCanExecuteChanged();
    }

    private void NotifyLayoutChanged()
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    private bool IsCurrentLoad(int version, string subagentId)
        => !_disposed
            && version == _loadVersion
            && string.Equals(
                SelectedSubagent?.SubagentId,
                subagentId,
                StringComparison.OrdinalIgnoreCase);

    private void BeginHydration(int version)
    {
        if (_disposed || version != _loadVersion)
        {
            return;
        }

        _isHydrating = true;
        NotifyHydrationStateChanged();
    }

    private void EndHydration(int version)
    {
        if (_disposed || version != _loadVersion || !_isHydrating)
        {
            return;
        }

        _isHydrating = false;
        NotifyHydrationStateChanged();
    }

    private void NotifyHydrationStateChanged()
    {
        OnPropertyChanged(nameof(IsHydrating));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(IsEditorEnabled));
        OnPropertyChanged(nameof(CanNavigateSubagents));
        NotifyDescriptionStateChanged();
        DeleteSubagentCommand.NotifyCanExecuteChanged();
        CreateSubagentCommand.NotifyCanExecuteChanged();
        BackToSubagentListCommand.NotifyCanExecuteChanged();
    }

    private void SetSelectionSilently(Action action)
    {
        _suppressSelectionHandlers = true;
        try
        {
            action();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private void ClearStatus() => SetStatus(string.Empty, SubagentStatusKind.None);

    private void SetStatus(string message, SubagentStatusKind kind, bool autoClear = false)
    {
        if (_disposed)
        {
            return;
        }

        _statusClear.Cancel();
        StatusKind = string.IsNullOrWhiteSpace(message) ? SubagentStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == SubagentStatusKind.Success)
        {
            _ = _statusClear.ScheduleAsync(SuccessStatusDisplayDuration, () =>
            {
                if (StatusKind == SubagentStatusKind.Success
                    && string.Equals(StatusText, message, StringComparison.Ordinal))
                {
                    ClearStatus();
                }
            });
        }
    }

    private async Task OpenProviderSettingsAsync(string? packageId)
    {
        if (_settingsNavigationService is null || string.IsNullOrWhiteSpace(packageId))
        {
            SetStatus("Package settings cannot be opened from this host.", SubagentStatusKind.Warning);
            return;
        }

        try
        {
            if (!await _settingsNavigationService.OpenPackageSettingsAsync(packageId))
            {
                SetStatus("Package settings could not be opened.", SubagentStatusKind.Warning);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SubagentStatusKind.Error);
        }
    }

    private void RunOnUiThread(Action action)
    {
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
    }

}
