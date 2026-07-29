using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RuntimeNoticeGracePeriod = TimeSpan.FromMilliseconds(1500);
    private static readonly ProfileEditorDraftComparer DraftComparer = new();
    private const string ListRefreshChannel = "profiles-list";
    private const string MutationChannel = "profiles-mutation";
    private const string CapabilitiesChannel = "profile-capabilities";
    private readonly IAgentProfileGateway _profileService;
    private readonly IAgentRuntimeAvailability? _runtimeAvailability;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly TimedStatusController _statusClear;
    private readonly TimedStatusController _runtimeNoticeDelay;
    private readonly OperationState<AgentProfileOperation> _operation = new();
    private readonly PresentationTaskScope _tasks;
    private readonly AsyncOnce _initialization = new();
    private readonly LatestRequestCoordinator _requests = new();
    private readonly KeyedAdaptiveListDetailState<string, AgentProfileRecord> _listDetail;
    private readonly SerializedRefreshLoop _runtimeRefresh;
    private readonly Dictionary<string, EditableDocumentState<ProfileEditorDraft>> _drafts =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressDraftTracking;
    private bool _initializationRefreshPending;
    private bool _isInitialized;
    private bool _disposed;
    private string? _initializationFailureStatus;
    private long _editRevision;
    private long _runtimeAvailabilityCallbackVersion;
    private bool _runtimeNoticePending;
    private Task _currentDetailLoad = Task.CompletedTask;

    public AgentProfilesViewModel(
        IAgentProfileGateway profileService,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(profileService, settingsNavigationService, PresentationDispatcher.Capture())
    {
    }

    internal AgentProfilesViewModel(
        IAgentProfileGateway profileService,
        IPackageSettingsNavigationService? settingsNavigationService,
        IPresentationDispatcher uiDispatcher,
        TimeProvider? runtimeNoticeTimeProvider = null)
    {
        _profileService = profileService;
        _runtimeAvailability = profileService as IAgentRuntimeAvailability;
        _settingsNavigationService = settingsNavigationService;
        _uiDispatcher = uiDispatcher;
        _tasks = new PresentationTaskScope();
        _statusClear = new TimedStatusController(dispatcher: uiDispatcher);
        _runtimeNoticeDelay = new TimedStatusController(runtimeNoticeTimeProvider, uiDispatcher);
        _listDetail = new KeyedAdaptiveListDetailState<string, AgentProfileRecord>(
            Profiles,
            static profile => profile.ProfileId,
            keyComparer: StringComparer.OrdinalIgnoreCase);
        _listDetail.SelectionChanging += OnProfileSelectionChanging;
        _listDetail.PropertyChanged += OnListDetailPropertyChanged;
        _runtimeRefresh = new SerializedRefreshLoop(
            RefreshProfileListAsync,
            ReportRuntimeRefreshFailure);
        ChatBinding = new ModelBindingEditorState(
            new ProviderModelLoader(AgentProfileProviderModelCatalog.CreateChat(profileService)),
            new ModelBindingEditorOptions(
                SelectFirstProvider: true,
                NoProvidersText: "No chat providers are installed.",
                NoProviderSelectedText: "No chat provider selected.",
                LoadingText: "Loading chat provider status...",
                LoadFailurePrefix: "Chat provider status"),
            uiDispatcher);
        EmbeddingBinding = new ModelBindingEditorState(
            new ProviderModelLoader(AgentProfileProviderModelCatalog.CreateEmbedding(profileService)),
            new ModelBindingEditorOptions(
                SelectFirstProvider: false,
                NoProvidersText: "No embedding providers are installed.",
                NoProviderSelectedText: "Embeddings are disabled.",
                LoadingText: "Loading embedding provider status...",
                LoadFailurePrefix: "Embedding provider status",
                EmptyProviderLabel: "Disabled (text and metadata only)"),
            uiDispatcher);
        ChatBinding.PropertyChanged += OnModelBindingPropertyChanged;
        EmbeddingBinding.PropertyChanged += OnModelBindingPropertyChanged;
        ChatBinding.Changed += OnEditorSelectionChanged;
        EmbeddingBinding.Changed += OnEditorSelectionChanged;
        Capabilities.Changed += OnCapabilitiesChanged;
        _operation.PropertyChanged += OnOperationPropertyChanged;
        _profileService.ProfileChanged += OnProfileChanged;
        _profileService.SelectableCapabilitiesChanged += OnSelectableCapabilitiesChanged;
        if (_runtimeAvailability is not null)
        {
            _runtimeAvailability.ConnectionStateChanged += OnRuntimeConnectionStateChanged;
        }
    }

    public ObservableCollection<AgentProfileRecord> Profiles { get; } = [];

    internal Task Initialization => InitializeAsync();

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await _initialization.RunAsync(InitializeCoreAsync, cancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_initializationFailureStatus is not null
                    && string.Equals(StatusText, _initializationFailureStatus, StringComparison.Ordinal))
                {
                    ClearStatus();
                }
                _initializationFailureStatus = null;
                ReconcileRuntimeNoticeState();
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_disposed)
                {
                    ClearEditor();
                    _initializationFailureStatus = ex.Message;
                    SetStatus(_initializationFailureStatus, AgentProfileStatusKind.Error);
                    ReconcileRuntimeNoticeState();
                }
            }).ConfigureAwait(false);
        }
    }

    internal ModelBindingEditorState ChatBinding { get; }

    internal ModelBindingEditorState EmbeddingBinding { get; }

    internal CapabilitySelectionState Capabilities { get; } = new();

    internal ObservableCollection<ProviderCatalogOption> ChatProviders => ChatBinding.Providers;

    internal ObservableCollection<ProviderModelCatalogOption> ChatModels => ChatBinding.Models;

    internal ObservableCollection<ModelReasoningOption> ReasoningOptions => ChatBinding.ReasoningOptions;

    internal ObservableCollection<ModelSpeedOption> SpeedOptions => ChatBinding.SpeedOptions;

    internal ObservableCollection<ModelModeOption> ModeOptions => ChatBinding.ModeOptions;

    internal ObservableCollection<ProviderCatalogOption> EmbeddingProviders => EmbeddingBinding.Providers;

    internal ObservableCollection<ProviderModelCatalogOption> EmbeddingModels => EmbeddingBinding.Models;

    internal ObservableCollection<CapabilityOptionState> LocalTools { get; } = [];

    internal ObservableCollection<CapabilityOptionState> PackageCapabilities { get; } = [];

    internal ObservableCollection<CapabilityGroupState> LocalToolGroups { get; } = [];

    internal ObservableCollection<CapabilityGroupState> PackageCapabilityGroups { get; } = [];

    internal ObservableCollection<CapabilityGroupState> CapabilityGroups { get; } = [];

    public ObservableCollection<BehaviorLoopOption> BehaviorLoops { get; } = [];

    public bool HasSelectedProfile => SelectedProfile is not null;

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => _listDetail.Layout == AdaptiveListDetailLayout.Wide;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    public bool IsDirty => SelectedProfile is not null
        && _drafts.TryGetValue(SelectedProfile.ProfileId, out var draft)
        && draft.IsDirty;

    public bool IsHydrating => _listDetail.DetailPhase == AdaptiveDetailPhase.Loading;

    public bool IsEditorEnabled => HasSelectedProfile && !IsHydrating;

    public bool CanNavigateProfiles => !_disposed && !IsHydrating;

    public AgentProfileRecord? SelectedProfile
    {
        get => _listDetail.SelectedItem;
        set
        {
            if (value is null)
            {
                ShowProfileListFromUserIntent();
            }
            else
            {
                ShowProfileFromUserIntent(value);
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
    private BehaviorLoopOption? _selectedBehaviorLoop;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private bool _hasEmbeddingConsumers;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private AgentProfileStatusKind _statusKind = AgentProfileStatusKind.None;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasRuntimeNotice))]
    private string _runtimeNoticeText = string.Empty;

    [ObservableProperty]
    private string _toolSelectionSummary = "No local tools are enabled for this profile.";

    [ObservableProperty]
    private string _packageCapabilitySelectionSummary =
        "No package capabilities are enabled for this profile.";

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

    internal ProviderCatalogOption? SelectedEmbeddingProvider
    {
        get => EmbeddingBinding.SelectedProvider;
        set => EmbeddingBinding.SelectedProvider = value;
    }

    internal ProviderModelCatalogOption? SelectedEmbeddingModel
    {
        get => EmbeddingBinding.SelectedModel;
        set => EmbeddingBinding.SelectedModel = value;
    }

    public bool IsBusy => _operation.IsBusy
        || IsHydrating
        || ChatBinding.IsLoading
        || EmbeddingBinding.IsLoading;

    public bool HasChatProviders => ChatBinding.HasProviders;

    public bool HasNoChatProviders => ChatBinding.HasNoProviders;

    public bool HasChatProviderWarning
    {
        get => ChatBinding.HasWarning;
        set => ChatBinding.SetWarningState(value);
    }

    public string ChatProviderWarningText => ChatBinding.WarningText;

    public string ChatProviderStatusText => ChatBinding.StatusText;

    public bool HasEmbeddingProviders => EmbeddingBinding.HasProviders;

    public bool HasNoEmbeddingProviders => EmbeddingBinding.HasNoProviders;

    public bool HasEmbeddingProviderWarning => EmbeddingBinding.HasWarning;

    public string EmbeddingProviderWarningText => EmbeddingBinding.WarningText;

    public string EmbeddingProviderStatusText => EmbeddingBinding.StatusText;

    public bool CanConfigureEmbeddings => HasEmbeddingProviders && HasEmbeddingConsumers;

    public bool CanSelectEmbeddingModel => CanConfigureEmbeddings
        && !HasEmbeddingProviderWarning
        && EmbeddingBinding.HasSelectedProvider
        && EmbeddingModels.Count > 0;

    public bool ShowEmbeddingsSection => HasEmbeddingConsumers;

    public bool ShowChatProviderPicker => ChatBinding.ShowProviderPicker;

    public bool ShowChatProviderWarning => ChatBinding.ShowProviderWarning;

    public bool ShowChatModelSelection => ChatBinding.ShowModelSelection;

    public bool ShowReasoningOptions => ChatBinding.ShowReasoningOptions;

    public bool ShowSpeedOptions => ChatBinding.ShowSpeedOptions;

    public bool ShowModeOptions => ChatBinding.ShowModeOptions;

    public bool IsReasoningSelectionEnabled => ChatBinding.IsReasoningSelectionEnabled;

    public bool HasReasoningOptions => ChatBinding.HasReasoningOptions;

    public bool HasSpeedOptions => ChatBinding.HasSpeedOptions;

    public bool HasModeOptions => ChatBinding.HasModeOptions;

    public bool ShowEmbeddingProviderPicker => CanConfigureEmbeddings;

    public bool ShowEmbeddingProviderEmptyState => HasEmbeddingConsumers && !HasEmbeddingProviders;

    public bool ShowEmbeddingProviderWarning => HasEmbeddingConsumers && EmbeddingBinding.ShowProviderWarning;

    public bool ShowEmbeddingModelSelection => CanSelectEmbeddingModel;

    public bool CanOpenChatProviderSettings => ChatBinding.CanOpenProviderSettings
        && _settingsNavigationService is not null;

    public bool CanOpenEmbeddingProviderSettings => EmbeddingBinding.CanOpenProviderSettings
        && _settingsNavigationService is not null;

    public bool HasLocalTools => LocalTools.Count > 0;

    public bool HasPackageCapabilities => PackageCapabilities.Count > 0;

    public bool HasToolCallingConfiguration => HasLocalTools || HasPackageCapabilities;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool HasRuntimeNotice => !string.IsNullOrWhiteSpace(RuntimeNoticeText);

    public bool IsStatusSuccess => StatusKind == AgentProfileStatusKind.Success;

    public bool IsStatusWarning => StatusKind == AgentProfileStatusKind.Warning;

    public bool IsStatusError => StatusKind == AgentProfileStatusKind.Error;

    partial void OnDisplayNameChanged(string value) => OnEditorChanged();

    partial void OnDescriptionChanged(string value) => OnEditorChanged();

    partial void OnInstructionsChanged(string value) => OnEditorChanged();

    partial void OnSelectedBehaviorLoopChanged(BehaviorLoopOption? value)
    {
        OnEditorChanged();
        if (!_suppressDraftTracking && SelectedProfile is not null)
        {
            _tasks.Run(_ => RefreshSelectedProfileCapabilitiesAsync());
        }
    }

    partial void OnHasEmbeddingConsumersChanged(bool value) => NotifyEmbeddingStateChanged();

    private void OnModelBindingPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(SelectedChatProvider));
        OnPropertyChanged(nameof(SelectedChatModel));
        OnPropertyChanged(nameof(SelectedReasoningOption));
        OnPropertyChanged(nameof(SelectedSpeedOption));
        OnPropertyChanged(nameof(SelectedModeOption));
        OnPropertyChanged(nameof(SelectedEmbeddingProvider));
        OnPropertyChanged(nameof(SelectedEmbeddingModel));
        OnPropertyChanged(nameof(HasChatProviders));
        OnPropertyChanged(nameof(HasNoChatProviders));
        OnPropertyChanged(nameof(HasChatProviderWarning));
        OnPropertyChanged(nameof(ChatProviderWarningText));
        OnPropertyChanged(nameof(ChatProviderStatusText));
        OnPropertyChanged(nameof(ShowChatProviderPicker));
        OnPropertyChanged(nameof(ShowChatProviderWarning));
        OnPropertyChanged(nameof(ShowChatModelSelection));
        OnPropertyChanged(nameof(HasReasoningOptions));
        OnPropertyChanged(nameof(HasSpeedOptions));
        OnPropertyChanged(nameof(HasModeOptions));
        OnPropertyChanged(nameof(ShowReasoningOptions));
        OnPropertyChanged(nameof(ShowSpeedOptions));
        OnPropertyChanged(nameof(ShowModeOptions));
        OnPropertyChanged(nameof(IsReasoningSelectionEnabled));
        OnPropertyChanged(nameof(CanOpenChatProviderSettings));
        NotifyEmbeddingStateChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private void NotifyEmbeddingStateChanged()
    {
        OnPropertyChanged(nameof(HasEmbeddingProviders));
        OnPropertyChanged(nameof(HasNoEmbeddingProviders));
        OnPropertyChanged(nameof(HasEmbeddingProviderWarning));
        OnPropertyChanged(nameof(EmbeddingProviderWarningText));
        OnPropertyChanged(nameof(EmbeddingProviderStatusText));
        OnPropertyChanged(nameof(CanConfigureEmbeddings));
        OnPropertyChanged(nameof(CanSelectEmbeddingModel));
        OnPropertyChanged(nameof(ShowEmbeddingsSection));
        OnPropertyChanged(nameof(ShowEmbeddingProviderPicker));
        OnPropertyChanged(nameof(ShowEmbeddingProviderEmptyState));
        OnPropertyChanged(nameof(ShowEmbeddingProviderWarning));
        OnPropertyChanged(nameof(ShowEmbeddingModelSelection));
        OnPropertyChanged(nameof(CanOpenEmbeddingProviderSettings));
    }

    private void OnEditorSelectionChanged() => OnEditorChanged();

    private void OnCapabilitiesChanged()
    {
        RefreshCapabilitySummaries();
        OnEditorChanged();
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

    private void ClearEditor()
    {
        _suppressDraftTracking = true;
        try
        {
            DisplayName = string.Empty;
            Description = string.Empty;
            Instructions = string.Empty;
            ChatBinding.Clear();
            EmbeddingBinding.Clear();
            BehaviorLoops.Clear();
            SelectedBehaviorLoop = null;
            Capabilities.Clear();
            LocalTools.Clear();
            PackageCapabilities.Clear();
            LocalToolGroups.Clear();
            PackageCapabilityGroups.Clear();
            CapabilityGroups.Clear();
            HasEmbeddingConsumers = false;
            RefreshCapabilitySummaries();
        }
        finally
        {
            _suppressDraftTracking = false;
        }

        OnPropertyChanged(nameof(IsDirty));
    }

    private void OnProfileChanged(string profileId) => RunOnUiThread(() =>
    {
        if (_isInitialized)
        {
            _tasks.Run(_runtimeRefresh.MarkDirty());
        }
        else
        {
            _initializationRefreshPending = true;
        }
    });

    private void OnSelectableCapabilitiesChanged()
        => RunOnUiThread(() =>
        {
            if (_isInitialized)
            {
                _tasks.Run(_ => RefreshSelectedProfileCapabilitiesAsync());
            }
        });

    private OperationGeneration BeginOperation(AgentProfileOperation operation)
    {
        if (_disposed)
        {
            return default;
        }

        var generation = _operation.Begin(operation, canCancel: false);
        OnPropertyChanged(nameof(IsBusy));
        SaveProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        return generation;
    }

    private void NotifyHydrationStateChanged()
    {
        OnPropertyChanged(nameof(IsHydrating));
        OnPropertyChanged(nameof(IsEditorEnabled));
        OnPropertyChanged(nameof(CanNavigateProfiles));
        OnPropertyChanged(nameof(IsBusy));
        SaveProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
        CreateProfileCommand.NotifyCanExecuteChanged();
        BackToProfileListCommand.NotifyCanExecuteChanged();
    }

    private void OnProfileSelectionChanging(AgentProfileRecord? previous, AgentProfileRecord? current)
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
        if (e.PropertyName == nameof(KeyedAdaptiveListDetailState<string, AgentProfileRecord>.SelectedItem))
        {
            OnPropertyChanged(nameof(SelectedProfile));
            OnPropertyChanged(nameof(HasSelectedProfile));
            OnPropertyChanged(nameof(IsDirty));
            if (_listDetail.IsExistingDetail && SelectedProfile is { } selected)
            {
                var ticket = _listDetail.BeginDetailLoad(_lifetimeCancellation.Token);
                _currentDetailLoad = LoadSelectedProfileAsync(selected, ticket);
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

    private void ShowProfileListFromUserIntent()
    {
        CancelPendingMutation();
        _listDetail.ShowList();
    }

    private void ShowProfileFromUserIntent(AgentProfileRecord profile)
    {
        if (_listDetail.IsExistingDetail && ReferenceEquals(SelectedProfile, profile))
        {
            return;
        }

        CancelPendingMutation();
        _listDetail.ShowExistingDetail(profile);
    }

    private void ReportRuntimeRefreshFailure(Exception exception)
        => RunOnUiThread(() => SetStatus(exception.Message, AgentProfileStatusKind.Error));

    private async Task RefreshProfileListAsync(CancellationToken cancellationToken)
        => _ = await TryRefreshProfileListAsync(cancellationToken).ConfigureAwait(false);

    private async Task<bool> TryRefreshProfileListAsync(CancellationToken cancellationToken)
    {
        var request = _requests.Begin(ListRefreshChannel, cancellationToken);
        var applied = false;
        try
        {
            var profiles = _profileService.ListProfiles();
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(request))
                {
                    _listDetail.Reconcile(profiles);
                    applied = true;
                }
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            request.CancellationToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _requests.Complete(request);
        }
        return applied;
    }

    private void EndOperation(OperationGeneration generation)
    {
        if (_disposed)
        {
            return;
        }

        _operation.TryComplete(generation);
        OnPropertyChanged(nameof(IsBusy));
        SaveProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private void ClearStatus() => SetStatus(string.Empty, AgentProfileStatusKind.None);

    private void SetStatus(string message, AgentProfileStatusKind kind, bool autoClear = false)
    {
        if (_disposed)
        {
            return;
        }

        _statusClear.Cancel();
        StatusKind = string.IsNullOrWhiteSpace(message) ? AgentProfileStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == AgentProfileStatusKind.Success)
        {
            _tasks.Run(_statusClear.ScheduleAsync(SuccessStatusDisplayDuration, () =>
            {
                if (StatusKind == AgentProfileStatusKind.Success
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
            SetStatus("Package settings cannot be opened from this host.", AgentProfileStatusKind.Warning);
            return;
        }

        try
        {
            if (!await _settingsNavigationService.OpenPackageSettingsAsync(packageId))
            {
                SetStatus("Package settings could not be opened.", AgentProfileStatusKind.Warning);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
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

    private void OnOperationPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        OnPropertyChanged(nameof(IsBusy));
        SaveProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private static AgentProfileModelBindingRecord? FindModelBinding(
        AgentProfileRecord profile,
        string capabilityKind)
    {
        var binding = profile.ModelBindings?.FirstOrDefault(candidate => string.Equals(
            candidate.CapabilityKind,
            capabilityKind,
            StringComparison.OrdinalIgnoreCase));
        if (binding is not null)
        {
            return binding;
        }

        var isChat = string.Equals(capabilityKind, AgentModelCapabilityKinds.Chat, StringComparison.OrdinalIgnoreCase);
        var providerId = isChat ? profile.ChatProviderId : profile.EmbeddingProviderId;
        var modelId = isChat ? profile.ChatModelId : profile.EmbeddingModelId;
        return string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId)
            ? null
            : new AgentProfileModelBindingRecord(
                profile.ProfileId,
                capabilityKind,
                providerId,
                modelId,
                SettingsJson: null,
                profile.UpdatedAtUtc);
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> values)
    {
        target.Clear();
        foreach (var value in values)
        {
            target.Add(value);
        }
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

}

internal enum AgentProfileOperation
{
    Create,
    Save,
    Delete,
    ReloadProviders,
    RefreshCapabilities,
}
