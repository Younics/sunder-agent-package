using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);
    private static readonly ProfileEditorDraftComparer DraftComparer = new();
    private readonly AgentProfileService _profileService;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly IPresentationDispatcher _uiDispatcher;
    private readonly TimedStatusController _statusClear;
    private readonly Task _initialization;
    private readonly Dictionary<string, EditableDocumentState<ProfileEditorDraft>> _drafts =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _suppressSelectionHandlers;
    private bool _suppressProfileChangeNotifications;
    private bool _suppressDraftTracking;
    private bool _isHydrating;
    private bool _disposed;
    private int _profileLoadVersion;
    private int _busyOperationCount;
    private long _editRevision;

    public AgentProfilesViewModel(
        AgentProfileService profileService,
        IPackageSettingsNavigationService? settingsNavigationService = null)
        : this(profileService, settingsNavigationService, PresentationDispatcher.Capture())
    {
    }

    internal AgentProfilesViewModel(
        AgentProfileService profileService,
        IPackageSettingsNavigationService? settingsNavigationService,
        IPresentationDispatcher uiDispatcher)
    {
        _profileService = profileService;
        _settingsNavigationService = settingsNavigationService;
        _uiDispatcher = uiDispatcher;
        _statusClear = new TimedStatusController(dispatcher: uiDispatcher);
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
        _profileService.ProfileChanged += OnProfileChanged;
        _profileService.SelectableCapabilitiesChanged += OnSelectableCapabilitiesChanged;
        _initialization = InitializeAsync();
    }

    public ObservableCollection<AgentProfileRecord> Profiles { get; } = [];

    internal Task Initialization => _initialization;

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

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    public bool IsDirty => SelectedProfile is not null
        && _drafts.TryGetValue(SelectedProfile.ProfileId, out var draft)
        && draft.IsDirty;

    public bool IsHydrating => _isHydrating;

    public bool IsEditorEnabled => HasSelectedProfile && !IsHydrating;

    public bool CanNavigateProfiles => !IsHydrating;

    [ObservableProperty]
    private AgentProfileRecord? _selectedProfile;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isEditorActive;

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

    public bool IsBusy => _busyOperationCount > 0
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

    public bool IsStatusSuccess => StatusKind == AgentProfileStatusKind.Success;

    public bool IsStatusWarning => StatusKind == AgentProfileStatusKind.Warning;

    public bool IsStatusError => StatusKind == AgentProfileStatusKind.Error;

    partial void OnSelectedProfileChanging(AgentProfileRecord? value)
    {
        if (!IsHydrating)
        {
            UpdateCurrentDraft();
        }
    }

    partial void OnSelectedProfileChanged(AgentProfileRecord? value)
    {
        DeleteProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedProfile));
        OnPropertyChanged(nameof(IsEditorEnabled));
        OnPropertyChanged(nameof(IsDirty));
        if (_suppressSelectionHandlers)
        {
            return;
        }

        _ = LoadSelectedProfileAsync(value, ++_profileLoadVersion);
        if (IsCompactLayout && value is not null)
        {
            IsEditorActive = true;
        }
    }

    partial void OnDisplayNameChanged(string value) => OnEditorChanged();

    partial void OnDescriptionChanged(string value) => OnEditorChanged();

    partial void OnInstructionsChanged(string value) => OnEditorChanged();

    partial void OnSelectedBehaviorLoopChanged(BehaviorLoopOption? value)
    {
        OnEditorChanged();
        if (!_suppressDraftTracking && SelectedProfile is not null)
        {
            _ = RefreshSelectedProfileCapabilitiesAsync();
        }
    }

    partial void OnHasEmbeddingConsumersChanged(bool value) => NotifyEmbeddingStateChanged();

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsEditorActive)
        {
            SelectedProfile = null;
        }
        else if (!value && SelectedProfile is null)
        {
            SelectedProfile = Profiles.FirstOrDefault();
        }

        NotifyLayoutChanged();
    }

    partial void OnIsEditorActiveChanged(bool value) => NotifyLayoutChanged();

    [RelayCommand(CanExecute = nameof(CanNavigateProfiles))]
    private async Task CreateProfileAsync()
    {
        BeginBusy();
        try
        {
            AgentProfileRecord created;
            _suppressProfileChangeNotifications = true;
            try
            {
                created = await _profileService.CreateProfileAsync("New Agent");
            }
            finally
            {
                _suppressProfileChangeNotifications = false;
            }

            await ReloadProfilesAsync(created.ProfileId);
            IsEditorActive = true;
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task SaveProfileAsync()
    {
        if (!CanEditProfile() || SelectedProfile is null)
        {
            return;
        }

        BeginBusy();
        try
        {
            var profileId = SelectedProfile.ProfileId;
            _suppressProfileChangeNotifications = true;
            try
            {
                _profileService.SaveProfile(
                    profileId,
                    string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed Profile" : DisplayName.Trim(),
                    Normalize(Description),
                    Normalize(Instructions),
                    ChatBinding.SelectedProvider?.Id,
                    ChatBinding.SelectedModel?.Id,
                    CanConfigureEmbeddings ? EmbeddingBinding.SelectedProvider?.Id : null,
                    CanConfigureEmbeddings ? EmbeddingBinding.SelectedModel?.Id : null,
                    Capabilities.Assignments,
                    SelectedBehaviorLoop?.LoopId ?? string.Empty,
                    SelectedBehaviorLoop?.SourceId ?? string.Empty,
                    SelectedProfile.BehaviorLoopSettingsJson ?? string.Empty,
                    ChatBinding.SettingsJson ?? string.Empty);
            }
            finally
            {
                _suppressProfileChangeNotifications = false;
            }

            _drafts.Remove(profileId);
            OnPropertyChanged(nameof(IsDirty));
            var shouldClearSelection = IsCompactLayout;
            await ReloadProfilesAsync(profileId);
            if (shouldClearSelection)
            {
                SelectedProfile = null;
                ClearStatus();
            }
            else
            {
                SetStatus("Profile saved.", AgentProfileStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEditProfile))]
    private async Task DeleteProfileAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        BeginBusy();
        try
        {
            var profileId = SelectedProfile.ProfileId;
            var deletedName = SelectedProfile.DisplayName;
            var shouldClearSelection = IsCompactLayout;
            _suppressProfileChangeNotifications = true;
            try
            {
                _profileService.DeleteProfile(profileId);
            }
            finally
            {
                _suppressProfileChangeNotifications = false;
            }

            _drafts.Remove(profileId);
            await ReloadProfilesAsync(selectProfileId: null);
            if (shouldClearSelection)
            {
                SelectedProfile = null;
                ClearStatus();
            }
            else
            {
                SetStatus($"Deleted profile '{deletedName}'.", AgentProfileStatusKind.Success, autoClear: true);
            }

            IsEditorActive = false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    private bool CanEditProfile() => SelectedProfile is not null && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanNavigateProfiles))]
    private void BackToProfileList()
    {
        UpdateCurrentDraft();
        if (IsCompactLayout)
        {
            SelectedProfile = null;
        }

        IsEditorActive = false;
    }

    [RelayCommand]
    private async Task ReloadProfileProvidersAsync()
    {
        if (SelectedProfile is null)
        {
            return;
        }

        BeginBusy();
        try
        {
            await Task.WhenAll(
                ChatBinding.RefreshAsync(ChatBinding.Selection, _lifetimeCancellation.Token),
                EmbeddingBinding.RefreshAsync(EmbeddingBinding.Selection, _lifetimeCancellation.Token));
            UpdateCurrentDraft();
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand]
    private Task OpenSelectedChatProviderSettingsAsync()
        => OpenProviderSettingsAsync(ChatBinding.SelectedProvider?.PackageId);

    [RelayCommand]
    private Task OpenSelectedEmbeddingProviderSettingsAsync()
        => OpenProviderSettingsAsync(EmbeddingBinding.SelectedProvider?.PackageId);

    [RelayCommand]
    private void OpenProfileEditor(AgentProfileRecord? profile)
    {
        if (profile is not null)
        {
            ActivateProfile(profile);
        }
    }

    public void ActivateProfile(AgentProfileRecord profile)
    {
        if (!CanNavigateProfiles)
        {
            return;
        }

        if (!string.Equals(
            SelectedProfile?.ProfileId,
            profile.ProfileId,
            StringComparison.OrdinalIgnoreCase))
        {
            SelectedProfile = profile;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            await ReloadProfilesAsync(selectProfileId: null);
        }
        catch (Exception ex)
        {
            ClearEditor();
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
    }

    private async Task ReloadProfilesAsync(string? selectProfileId)
    {
        AgentProfileRecord? profileToLoad = null;
        BeginBusy();
        try
        {
            var currentProfileId = SelectedProfile?.ProfileId;
            SetSelectionSilently(() =>
            {
                Profiles.Clear();
                foreach (var profile in _profileService.ListProfiles())
                {
                    Profiles.Add(profile);
                }

                var selected = Profiles.FirstOrDefault(profile => string.Equals(
                    profile.ProfileId,
                    selectProfileId,
                    StringComparison.OrdinalIgnoreCase));
                if (selected is null && (!IsCompactLayout || selectProfileId is not null))
                {
                    selected = Profiles.FirstOrDefault(profile => string.Equals(
                            profile.ProfileId,
                            currentProfileId,
                            StringComparison.OrdinalIgnoreCase))
                        ?? Profiles.FirstOrDefault();
                }

                SelectedProfile = selected;
            });

            if (SelectedProfile is null)
            {
                ClearEditor();
                SetStatus(
                    Profiles.Count == 0 ? "No profiles available." : string.Empty,
                    Profiles.Count == 0 ? AgentProfileStatusKind.Warning : AgentProfileStatusKind.None);
            }
            else
            {
                profileToLoad = SelectedProfile;
            }
        }
        finally
        {
            EndBusy();
        }

        if (profileToLoad is not null)
        {
            await LoadSelectedProfileAsync(profileToLoad, ++_profileLoadVersion);
        }
    }

    private async Task LoadSelectedProfileAsync(AgentProfileRecord? profile, int version)
    {
        if (profile is null)
        {
            ClearEditor();
            EndHydration(version);
            return;
        }

        BeginHydration(version);
        var startEditRevision = _editRevision;
        try
        {
            var hasDraft = _drafts.TryGetValue(profile.ProfileId, out var document);
            var preserveDirtyDraft = hasDraft && document!.IsDirty;
            var draft = preserveDirtyDraft
                ? document!.Value
                : CreatePersistedDraft(profile);
            if (!preserveDirtyDraft)
            {
                document = new EditableDocumentState<ProfileEditorDraft>(draft, DraftComparer);
                _drafts[profile.ProfileId] = document;
            }

            var localToolsTask = _profileService.ListInstalledLocalToolsAsync(_lifetimeCancellation.Token);
            var packageCapabilitiesTask = _profileService.ListSelectableProfileCapabilitiesAsync(
                BuildCapabilityRequestProfile(profile, draft),
                _lifetimeCancellation.Token);
            var behaviorLoops = _profileService.ListBehaviorLoops()
                .Select(loop => new BehaviorLoopOption(
                    loop.Descriptor.LoopId,
                    loop.Descriptor.SourceId,
                    loop.Descriptor.DisplayName,
                    loop.Descriptor.Description))
                .ToArray();

            _suppressDraftTracking = true;
            try
            {
                DisplayName = draft.DisplayName;
                Description = draft.Description;
                Instructions = draft.Instructions;
                HasEmbeddingConsumers = _profileService.HasProfileCapabilityConsumers(
                    AgentModelCapabilityKinds.Embedding);
            }
            finally
            {
                _suppressDraftTracking = false;
            }

            await Task.WhenAll(
                ChatBinding.RefreshAsync(draft.ChatBinding, _lifetimeCancellation.Token),
                EmbeddingBinding.RefreshAsync(draft.EmbeddingBinding, _lifetimeCancellation.Token),
                localToolsTask,
                packageCapabilitiesTask).ConfigureAwait(false);
            var localTools = await localToolsTask.ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentProfileLoad(version, profile.ProfileId))
                {
                    return;
                }

                _suppressDraftTracking = true;
                try
                {
                    ApplyBehaviorLoopSelection(behaviorLoops, draft.BehaviorLoopId, draft.BehaviorLoopSourceId);
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
                    _drafts[profile.ProfileId] = new EditableDocumentState<ProfileEditorDraft>(
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
                if (!_disposed && IsCurrentProfileLoad(version, profile.ProfileId))
                {
                    SetStatus(ex.Message, AgentProfileStatusKind.Error);
                }
            }).ConfigureAwait(false);
        }
        finally
        {
            await _uiDispatcher.InvokeAsync(() => EndHydration(version)).ConfigureAwait(false);
        }
    }

    private async Task RefreshSelectedProfileCapabilitiesAsync()
    {
        var profile = SelectedProfile;
        if (profile is null)
        {
            return;
        }

        var version = _profileLoadVersion;
        BeginBusy();
        try
        {
            var requestProfile = BuildCapabilityRequestProfile(profile, CaptureDraft());
            var localToolsTask = _profileService.ListInstalledLocalToolsAsync(_lifetimeCancellation.Token);
            var packageCapabilitiesTask = _profileService.ListSelectableProfileCapabilitiesAsync(
                requestProfile,
                _lifetimeCancellation.Token);
            await Task.WhenAll(localToolsTask, packageCapabilitiesTask).ConfigureAwait(false);
            var localTools = await localToolsTask.ConfigureAwait(false);
            var packageCapabilities = await packageCapabilitiesTask.ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentProfileLoad(version, profile.ProfileId))
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
                if (!_disposed && IsCurrentProfileLoad(version, profile.ProfileId))
                {
                    SetStatus(ex.Message, AgentProfileStatusKind.Error);
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
            }).ConfigureAwait(false);
        }
    }

    private void ApplyCapabilityOptions(
        IReadOnlyList<AgentToolCatalogEntry> localTools,
        IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> packageCapabilities,
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> assignments,
        bool preserveCurrent)
    {
        var definitions = localTools.Select(item =>
            {
                var descriptor = item.Descriptor;
                var aliases = descriptor.Aliases?
                    .Where(alias => !string.IsNullOrWhiteSpace(alias))
                    .Select(alias => new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        alias,
                        descriptor.SourceId))
                    .ToArray();
                return new CapabilityOptionDefinition(
                    "local",
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
            .Concat(packageCapabilities.Select(capability => new CapabilityOptionDefinition(
                "package",
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

        Replace(LocalTools, Capabilities.GetOptions("local"));
        Replace(PackageCapabilities, Capabilities.GetOptions("package"));
        Replace(LocalToolGroups, Capabilities.GetGroups("local"));
        Replace(PackageCapabilityGroups, Capabilities.GetGroups("package"));
        Replace(CapabilityGroups, Capabilities.Groups);
        RefreshCapabilitySummaries();
        OnPropertyChanged(nameof(HasLocalTools));
        OnPropertyChanged(nameof(HasPackageCapabilities));
        OnPropertyChanged(nameof(HasToolCallingConfiguration));
    }

    private void ApplyBehaviorLoopSelection(
        IReadOnlyList<BehaviorLoopOption> availableLoops,
        string? selectedLoopId,
        string? selectedSourceId)
    {
        Replace(BehaviorLoops, availableLoops);
        SelectedBehaviorLoop = BehaviorLoops.FirstOrDefault(option =>
                !string.IsNullOrWhiteSpace(selectedLoopId)
                && string.Equals(option.LoopId, selectedLoopId, StringComparison.OrdinalIgnoreCase)
                && (string.IsNullOrWhiteSpace(selectedSourceId)
                    || string.Equals(option.SourceId, selectedSourceId, StringComparison.OrdinalIgnoreCase)))
            ?? BehaviorLoops.FirstOrDefault(option => string.Equals(
                option.LoopId,
                "default",
                StringComparison.OrdinalIgnoreCase))
            ?? BehaviorLoops.FirstOrDefault();
    }

    private void RefreshCapabilitySummaries()
    {
        var enabledTools = LocalTools.Where(option => option.IsEnabled).Select(option => option.DisplayName).ToArray();
        ToolSelectionSummary = enabledTools.Length == 0
            ? "No local tools are enabled for this profile."
            : $"Enabled local tools: {string.Join(", ", enabledTools)}";
        var enabledPackages = PackageCapabilities.Where(option => option.IsEnabled).Select(option => option.DisplayName).ToArray();
        PackageCapabilitySelectionSummary = enabledPackages.Length == 0
            ? "No package capabilities are enabled for this profile."
            : $"Enabled package capabilities: {string.Join(", ", enabledPackages)}";
    }

    private ProfileEditorDraft CreatePersistedDraft(AgentProfileRecord profile)
    {
        var chatBinding = FindModelBinding(profile, AgentModelCapabilityKinds.Chat);
        var embeddingBinding = FindModelBinding(profile, AgentModelCapabilityKinds.Embedding);
        return new ProfileEditorDraft(
            profile.DisplayName,
            profile.Description ?? string.Empty,
            profile.Instructions ?? string.Empty,
            new ModelBindingSelection(chatBinding?.ProviderId, chatBinding?.ModelId, chatBinding?.SettingsJson),
            new ModelBindingSelection(embeddingBinding?.ProviderId, embeddingBinding?.ModelId, embeddingBinding?.SettingsJson),
            profile.BehaviorLoopId,
            profile.BehaviorLoopSourceId,
            profile.SelectableCapabilityAssignments ?? []);
    }

    private ProfileEditorDraft CaptureDraft() => new(
        DisplayName,
        Description,
        Instructions,
        ChatBinding.Selection,
        EmbeddingBinding.Selection,
        SelectedBehaviorLoop?.LoopId,
        SelectedBehaviorLoop?.SourceId,
        Capabilities.Assignments);

    private void UpdateCurrentDraft()
    {
        if (_suppressDraftTracking || SelectedProfile is null
            || !_drafts.TryGetValue(SelectedProfile.ProfileId, out var document))
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

    private AgentProfileRecord BuildCapabilityRequestProfile(
        AgentProfileRecord profile,
        ProfileEditorDraft draft)
        => profile with
        {
            SelectableCapabilityAssignments = draft.CapabilityAssignments,
            BehaviorLoopId = draft.BehaviorLoopId ?? profile.BehaviorLoopId,
            BehaviorLoopSourceId = draft.BehaviorLoopSourceId ?? profile.BehaviorLoopSourceId,
        };

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
        if (!_suppressProfileChangeNotifications)
        {
            _ = ReloadProfilesSafelyAsync(SelectedProfile?.ProfileId);
        }
    });

    private void OnSelectableCapabilitiesChanged()
        => RunOnUiThread(() => _ = RefreshSelectedProfileCapabilitiesAsync());

    private async Task ReloadProfilesSafelyAsync(string? selectProfileId)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            await ReloadProfilesAsync(selectProfileId);
        }
        catch (Exception ex)
        {
            ClearEditor();
            SetStatus(ex.Message, AgentProfileStatusKind.Error);
        }
    }

    private bool IsCurrentProfileLoad(int version, string profileId)
        => !_disposed
            && version == _profileLoadVersion
            && string.Equals(SelectedProfile?.ProfileId, profileId, StringComparison.OrdinalIgnoreCase);

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

    private void BeginBusy()
    {
        if (_disposed)
        {
            return;
        }

        _busyOperationCount++;
        OnPropertyChanged(nameof(IsBusy));
        SaveProfileCommand.NotifyCanExecuteChanged();
        DeleteProfileCommand.NotifyCanExecuteChanged();
    }

    private void BeginHydration(int version)
    {
        if (_disposed || version != _profileLoadVersion)
        {
            return;
        }

        _isHydrating = true;
        NotifyHydrationStateChanged();
    }

    private void EndHydration(int version)
    {
        if (_disposed || version != _profileLoadVersion || !_isHydrating)
        {
            return;
        }

        _isHydrating = false;
        NotifyHydrationStateChanged();
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

    private void EndBusy()
    {
        if (_disposed)
        {
            return;
        }

        if (_busyOperationCount > 0)
        {
            _busyOperationCount--;
        }

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
            _ = _statusClear.ScheduleAsync(SuccessStatusDisplayDuration, () =>
            {
                if (StatusKind == AgentProfileStatusKind.Success
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
        _ = _uiDispatcher.InvokeAsync(() =>
        {
            if (!_disposed)
            {
                action();
            }
        });
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
