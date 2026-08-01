using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
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
    private const string ListRefreshChannel = "subagents-list";
    private const string MutationChannel = "subagents-mutation";
    private const string CapabilitiesChannel = "subagent-capabilities";
    private readonly ISubagentManagementGateway? _gateway;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly TimedStatusController _statusClear;
    private readonly PresentationTaskScope _tasks;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly OperationState<SubagentOperation> _operation = new();
    private readonly AsyncOnce _initialization = new();
    private readonly LatestRequestCoordinator _requests = new();
    private readonly KeyedAdaptiveListDetailState<string, SubagentRecord> _listDetail;
    private readonly SerializedRefreshLoop _runtimeRefresh;
    private readonly Dictionary<string, EditableDocumentState<SubagentEditorDraft>> _drafts =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressDraftTracking;
    private bool _disposed;
    private string? _initializationFailureStatus;
    private long _editRevision;
    private Task _currentDetailLoad = Task.CompletedTask;

    public SubagentsViewModel(
        SubagentService subagentService,
        AgentRpcCatalog rpcCatalog,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(
            new SubagentLocalManagementGateway(subagentService, rpcCatalog),
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
        _listDetail = new KeyedAdaptiveListDetailState<string, SubagentRecord>(
            Subagents,
            static subagent => subagent.SubagentId,
            keyComparer: StringComparer.OrdinalIgnoreCase);
        _listDetail.SelectionChanging += OnSubagentSelectionChanging;
        _listDetail.PropertyChanged += OnListDetailPropertyChanged;
        _runtimeRefresh = new SerializedRefreshLoop(ReloadAsync, ReportRuntimeRefreshFailure);
        ChatBinding = SubagentModelBindingEditor.Create(
            new ProviderModelCatalogAdapter(gateway.ListChatProviders, gateway.LoadChatModelsAsync),
            uiDispatcher);
        Subscribe();
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
        _listDetail = new KeyedAdaptiveListDetailState<string, SubagentRecord>(
            Subagents,
            static subagent => subagent.SubagentId,
            keyComparer: StringComparer.OrdinalIgnoreCase);
        _listDetail.SelectionChanging += OnSubagentSelectionChanging;
        _listDetail.PropertyChanged += OnListDetailPropertyChanged;
        _runtimeRefresh = new SerializedRefreshLoop(ReloadAsync, ReportRuntimeRefreshFailure);
        ChatBinding = SubagentModelBindingEditor.Create(new ProviderModelCatalogAdapter(
            () => [],
            (_, _) => Task.FromResult(new ProviderModelCatalogResult([], string.Empty))),
            _uiDispatcher);
        Subscribe();
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

    public bool ShowWideLayout => _listDetail.Layout == AdaptiveListDetailLayout.Wide;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    public bool IsDirty => SelectedSubagent is not null
        && _drafts.TryGetValue(SelectedSubagent.SubagentId, out var draft)
        && draft.IsDirty;

    public bool IsHydrating => _listDetail.DetailPhase == AdaptiveDetailPhase.Loading;

    public bool IsBusy => _operation.IsBusy || IsHydrating || ChatBinding.IsLoading;

    public bool IsEditorEnabled => HasSelectedSubagent && !IsHydrating;

    public bool CanNavigateSubagents => !_disposed && !IsHydrating;

    public SubagentRecord? SelectedSubagent
    {
        get => _listDetail.SelectedItem;
        set
        {
            if (value is null)
            {
                ShowSubagentListFromUserIntent();
            }
            else
            {
                ShowSubagentFromUserIntent(value);
            }
        }
    }

    public bool IsCompactLayout
    {
        get => _listDetail.Layout == AdaptiveListDetailLayout.Compact;
        set => _listDetail.SetLayout(value
            ? AdaptiveListDetailLayout.Compact
            : AdaptiveListDetailLayout.Wide);
    }

    public bool IsEditorActive => IsCompactLayout && !_listDetail.IsList;

    internal AdaptiveListDetailRoute Route => _listDetail.Route;

    internal AdaptiveListDetailLayout Layout => _listDetail.Layout;

    internal AdaptiveDetailPhase DetailPhase => _listDetail.DetailPhase;

    internal long IntentRevision => _listDetail.IntentRevision;

    internal long LayoutRevision => _listDetail.LayoutRevision;

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

    partial void OnDisplayNameChanged(string value) => OnEditorChanged();

    partial void OnDescriptionChanged(string value)
    {
        NotifyDescriptionStateChanged();
        OnEditorChanged();
    }

    partial void OnInstructionsChanged(string value) => OnEditorChanged();

    private async Task ReloadAsync(
        CancellationToken cancellationToken = default)
    {
        if (_gateway is null)
        {
            ClearEditor();
            return;
        }

        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _lifetimeCancellation.Token);
        var request = _requests.Begin(ListRefreshChannel, linkedCancellation.Token);
        try
        {
            var subagents = await _gateway.ListSubagentsAsync(request.CancellationToken)
                .WaitAsync(request.CancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_requests.IsCurrent(request))
                {
                    return;
                }

                _listDetail.Reconcile(subagents);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    private async Task LoadSelectedSubagentAsync(
        SubagentRecord subagent,
        AdaptiveDetailTicket<string> ticket)
    {
        var cancellationToken = ticket.Request.CancellationToken;
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

            var localToolsTask = _gateway?.ListLocalToolsAsync(cancellationToken)
                ?? Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);
            var packageCapabilitiesTask = _gateway?.ListPackageCapabilitiesAsync(cancellationToken)
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
                    ChatBinding.RefreshAsync(draft.ChatBinding, cancellationToken),
                    localToolsTask,
                    packageCapabilitiesTask)
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            var localTools = await localToolsTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_listDetail.IsCurrentDetail(ticket))
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
                _listDetail.TrySetDetailReady(ticket);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await _uiDispatcher.InvokeAsync(() =>
                _listDetail.TryCancelDetailLoad(ticket)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_listDetail.TrySetDetailError(ticket, ex))
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
    }

    private async Task RefreshSelectedSubagentCapabilitiesAsync()
    {
        var subagent = SelectedSubagent;
        if (subagent is null)
        {
            return;
        }

        var intentRevision = IntentRevision;
        var request = _requests.Begin(CapabilitiesChannel, _lifetimeCancellation.Token);
        var operation = BeginOperation(SubagentOperation.RefreshCapabilities);
        try
        {
            var localToolsTask = _gateway?.ListLocalToolsAsync(request.CancellationToken)
                ?? Task.FromResult<IReadOnlyList<AgentToolDescriptor>>([]);
            var packageCapabilitiesTask = _gateway?.ListPackageCapabilitiesAsync(request.CancellationToken)
                ?? Task.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
            await Task.WhenAll(localToolsTask, packageCapabilitiesTask)
                .WaitAsync(request.CancellationToken)
                .ConfigureAwait(false);
            var localTools = await localToolsTask.WaitAsync(request.CancellationToken).ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.WaitAsync(request.CancellationToken).ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_requests.IsCurrent(request)
                    || intentRevision != IntentRevision
                    || !string.Equals(
                        SelectedSubagent?.SubagentId,
                        subagent.SubagentId,
                        StringComparison.OrdinalIgnoreCase))
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
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(request)
                    && intentRevision == IntentRevision
                    && string.Equals(
                        SelectedSubagent?.SubagentId,
                        subagent.SubagentId,
                        StringComparison.OrdinalIgnoreCase))
                {
                    SetStatus(ex.Message, SubagentStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            _requests.Complete(request);
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

    private void OnSubagentSelectionChanging(SubagentRecord? previous, SubagentRecord? current)
    {
        if (!IsHydrating)
        {
            UpdateCurrentDraft();
        }
    }

    private void OnListDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_listDetail.IsExistingDetail)
        {
            _currentDetailLoad = Task.CompletedTask;
        }
        if (e.PropertyName == nameof(KeyedAdaptiveListDetailState<string, SubagentRecord>.SelectedItem))
        {
            OnPropertyChanged(nameof(SelectedSubagent));
            OnPropertyChanged(nameof(HasSelectedSubagent));
            OnPropertyChanged(nameof(IsDirty));
            if (_listDetail.IsExistingDetail && SelectedSubagent is { } selected)
            {
                var ticket = _listDetail.BeginDetailLoad(_lifetimeCancellation.Token);
                _currentDetailLoad = LoadSelectedSubagentAsync(selected, ticket);
                _tasks.Run(_currentDetailLoad);
            }
            else if (_listDetail.IsList)
            {
                ClearEditor();
            }
        }

        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(IsEditorActive));
        NotifyLayoutChanged();
        NotifyHydrationStateChanged();
    }

    private void CancelPendingMutation()
    {
        _requests.Invalidate(MutationChannel);
        _operation.CancelCurrent();
    }

    private void ShowSubagentListFromUserIntent()
    {
        CancelPendingMutation();
        _listDetail.ShowList();
    }

    private void ShowSubagentFromUserIntent(SubagentRecord subagent)
    {
        if (_listDetail.IsExistingDetail && ReferenceEquals(SelectedSubagent, subagent))
        {
            return;
        }

        CancelPendingMutation();
        _listDetail.ShowExistingDetail(subagent);
    }

    private void ReportRuntimeRefreshFailure(Exception exception)
        => RunOnUiThread(() => SetStatus(exception.Message, SubagentStatusKind.Error));

}
