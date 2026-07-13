using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubagentsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);
    private static readonly SubagentEditorDraftComparer DraftComparer = new();
    private readonly ISubagentManagementGateway? _gateway;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly TimedStatusController _statusClear;
    private readonly PresentationTaskScope _tasks;
    private readonly OperationState<SubagentOperation> _operation = new();
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
            new SubagentLocalManagementGateway(subagentService, extensionCatalog),
            settingsNavigationService,
            PresentationDispatcher.Capture())
    {
    }

    internal SubagentsViewModel(
        ISubagentManagementGateway gateway,
        IPackageSettingsNavigationService? settingsNavigationService,
        IPresentationDispatcher uiDispatcher)
    {
        _gateway = gateway;
        _settingsNavigationService = settingsNavigationService;
        _uiDispatcher = uiDispatcher;
        _tasks = new PresentationTaskScope();
        _statusClear = new TimedStatusController(dispatcher: uiDispatcher);
        ChatBinding = SubagentModelBindingEditor.Create(
            new ProviderModelCatalogAdapter(gateway.ListChatProviders, gateway.LoadChatModelsAsync),
            uiDispatcher);
        Subscribe();
        _initialization = InitializeCoreAsync();
    }

    internal SubagentsViewModel(
        ISubagentManagementGateway gateway,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(gateway, settingsNavigationService, PresentationDispatcher.Capture())
    {
    }

    public SubagentsViewModel()
    {
        _uiDispatcher = PresentationDispatcher.Capture();
        _tasks = new PresentationTaskScope();
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

    public bool IsBusy => _operation.IsBusy || IsHydrating || ChatBinding.IsLoading;

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

        _tasks.Run(_ => LoadSelectedSubagentAsync(value, ++_loadVersion));
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

    private async Task ReloadAsync(string? selectedSubagentId)
    {
        if (_gateway is null)
        {
            ClearEditor();
            return;
        }

        var currentSubagentId = SelectedSubagent?.SubagentId;
        var subagents = await _gateway.ListSubagentsAsync();
        SetSelectionSilently(() =>
        {
            Subagents.Clear();
            foreach (var subagent in subagents)
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

            var localToolsTask = _gateway?.ListLocalToolsAsync()
                ?? Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);
            var packageCapabilitiesTask = _gateway?.ListPackageCapabilitiesAsync()
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
        var operation = BeginOperation(SubagentOperation.RefreshCapabilities);
        try
        {
            var localToolsTask = _gateway?.ListLocalToolsAsync()
                ?? Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);
            var packageCapabilitiesTask = _gateway?.ListPackageCapabilitiesAsync()
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
        finally
        {
            await _uiDispatcher.InvokeAsync(() => EndOperation(operation)).ConfigureAwait(false);
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
            _tasks.Run(_statusClear.ScheduleAsync(SuccessStatusDisplayDuration, () =>
            {
                if (StatusKind == SubagentStatusKind.Success
                    && string.Equals(StatusText, message, StringComparison.Ordinal))
                {
                    ClearStatus();
                }
            }));
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
        _tasks.Run(_uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                action();
            }
        }));
    }

}
