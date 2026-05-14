using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentProfilesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly AgentProfileService _profileService;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private CancellationTokenSource? _successStatusClearCancellation;
    private bool _suppressSelectionHandlers;
    private bool _disposed;
    private int _profileLoadVersion;
    private int _chatLoadVersion;
    private int _embeddingLoadVersion;
    private int _busyOperationCount;
    private string? _loadedCapabilityProfileId;
    private IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> _preservedSelectableCapabilityAssignments =
    [];

    public AgentProfilesViewModel(
        AgentProfileService profileService,
        IPackageSettingsNavigationService? settingsNavigationService = null
    )
    {
        _profileService = profileService;
        _settingsNavigationService = settingsNavigationService;
        _profileService.SelectableCapabilitiesChanged += OnSelectableCapabilitiesChanged;
        _ = InitializeAsync();
    }

    public ObservableCollection<AgentProfileRecord> Profiles { get; } = [];

    public ObservableCollection<ProviderOption> ChatProviders { get; } = [];

    public ObservableCollection<ModelOption> ChatModels { get; } = [];

    public ObservableCollection<ModelReasoningOption> ReasoningOptions { get; } = [];

    public ObservableCollection<ProviderOption> EmbeddingProviders { get; } = [];

    public ObservableCollection<ModelOption> EmbeddingModels { get; } = [];

    public ObservableCollection<BehaviorLoopOption> BehaviorLoops { get; } = [];

    public ObservableCollection<ProfileCapabilityOptionViewModel> LocalTools { get; } = [];

    public ObservableCollection<ProfileCapabilityOptionViewModel> PackageCapabilities { get; } = [];

    public ObservableCollection<ProfileCapabilityGroupViewModel> LocalToolGroups { get; } = [];

    public ObservableCollection<ProfileCapabilityGroupViewModel> PackageCapabilityGroups { get; } =
        [];

    public ObservableCollection<ProfileCapabilityGroupViewModel> CapabilityGroups { get; } = [];

    public bool HasSelectedProfile => SelectedProfile is not null;

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    [ObservableProperty]
    private AgentProfileRecord? _selectedProfile;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isEditorActive;

    [ObservableProperty]
    private ProviderOption? _selectedChatProvider;

    [ObservableProperty]
    private ModelOption? _selectedChatModel;

    [ObservableProperty]
    private ModelReasoningOption? _selectedReasoningOption;

    [ObservableProperty]
    private ProviderOption? _selectedEmbeddingProvider;

    [ObservableProperty]
    private ModelOption? _selectedEmbeddingModel;

    [ObservableProperty]
    private BehaviorLoopOption? _selectedBehaviorLoop;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private string _instructions = string.Empty;

    [ObservableProperty]
    private string _chatProviderStatusText = "No chat provider selected.";

    [ObservableProperty]
    private string _embeddingProviderStatusText = "Embeddings are disabled.";

    [ObservableProperty]
    private bool _hasChatProviders;

    [ObservableProperty]
    private bool _hasChatProviderWarning;

    [ObservableProperty]
    private string _chatProviderWarningText = string.Empty;

    [ObservableProperty]
    private bool _hasEmbeddingProviderWarning;

    [ObservableProperty]
    private string _embeddingProviderWarningText = string.Empty;

    [ObservableProperty]
    private bool _hasEmbeddingProviders;

    [ObservableProperty]
    private bool _hasEmbeddingConsumers;

    [ObservableProperty]
    private bool _hasLocalTools;

    [ObservableProperty]
    private bool _hasPackageCapabilities;

    [ObservableProperty]
    private bool _isBusy;

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

    public bool CanConfigureEmbeddings => HasEmbeddingProviders && HasEmbeddingConsumers;

    public bool CanSelectEmbeddingModel =>
        CanConfigureEmbeddings
        && !HasEmbeddingProviderWarning
        && SelectedEmbeddingProvider?.Id is not null
        && EmbeddingModels.Count > 0;

    public bool HasNoChatProviders => !HasChatProviders;

    public bool HasNoEmbeddingProviders => !HasEmbeddingProviders;

    public bool ShowEmbeddingsSection => HasEmbeddingConsumers;

    public bool ShowChatProviderPicker => HasChatProviders;

    public bool ShowChatProviderWarning => HasChatProviderWarning;

    public bool ShowChatModelSelection =>
        HasChatProviders && !HasChatProviderWarning && SelectedChatProvider?.Id is not null;

    public bool ShowReasoningOptions => ShowChatModelSelection && HasReasoningOptions;

    public bool ShowEmbeddingProviderPicker => CanConfigureEmbeddings;

    public bool ShowEmbeddingProviderEmptyState => HasEmbeddingConsumers && !HasEmbeddingProviders;

    public bool ShowEmbeddingProviderWarning => HasEmbeddingProviderWarning;

    public bool ShowEmbeddingModelSelection => CanSelectEmbeddingModel;

    public bool CanOpenChatProviderSettings =>
        ShowChatProviderWarning
        && _settingsNavigationService is not null
        && !string.IsNullOrWhiteSpace(SelectedChatProvider?.PackageId);

    public bool CanOpenEmbeddingProviderSettings =>
        ShowEmbeddingProviderWarning
        && _settingsNavigationService is not null
        && !string.IsNullOrWhiteSpace(SelectedEmbeddingProvider?.PackageId);

    public bool HasToolCallingConfiguration => HasLocalTools || HasPackageCapabilities;

    public bool HasReasoningOptions => ReasoningOptions.Count > 0;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsStatusSuccess => StatusKind == AgentProfileStatusKind.Success;

    public bool IsStatusWarning => StatusKind == AgentProfileStatusKind.Warning;

    public bool IsStatusError => StatusKind == AgentProfileStatusKind.Error;

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

    partial void OnIsBusyChanged(bool value)
    {
        DeleteProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
    }

    partial void OnHasChatProvidersChanged(bool value) => NotifyChatProviderStateChanged();

    partial void OnHasChatProviderWarningChanged(bool value) => NotifyChatProviderStateChanged();

    partial void OnHasEmbeddingProviderWarningChanged(bool value) =>
        NotifyEmbeddingProviderStateChanged();

    partial void OnHasEmbeddingProvidersChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureEmbeddings));
        OnPropertyChanged(nameof(CanSelectEmbeddingModel));
        OnPropertyChanged(nameof(HasNoEmbeddingProviders));
        OnPropertyChanged(nameof(ShowEmbeddingProviderPicker));
        OnPropertyChanged(nameof(ShowEmbeddingProviderEmptyState));
        OnPropertyChanged(nameof(ShowEmbeddingModelSelection));
    }

    partial void OnHasEmbeddingConsumersChanged(bool value)
    {
        OnPropertyChanged(nameof(CanConfigureEmbeddings));
        OnPropertyChanged(nameof(CanSelectEmbeddingModel));
        OnPropertyChanged(nameof(ShowEmbeddingsSection));
        OnPropertyChanged(nameof(ShowEmbeddingProviderPicker));
        OnPropertyChanged(nameof(ShowEmbeddingProviderEmptyState));
        OnPropertyChanged(nameof(ShowEmbeddingModelSelection));
    }

    partial void OnHasLocalToolsChanged(bool value) =>
        OnPropertyChanged(nameof(HasToolCallingConfiguration));

    partial void OnHasPackageCapabilitiesChanged(bool value) =>
        OnPropertyChanged(nameof(HasToolCallingConfiguration));

    partial void OnSelectedProfileChanged(AgentProfileRecord? value)
    {
        DeleteProfileCommand.NotifyCanExecuteChanged();
        SaveProfileCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedProfile));

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

        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    partial void OnIsEditorActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactEditor));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowEditorPane));
    }

    partial void OnSelectedChatProviderChanged(ProviderOption? value)
    {
        NotifyChatProviderStateChanged();
        if (_suppressSelectionHandlers || SelectedProfile is null)
        {
            return;
        }

        var chatBinding = FindModelBinding(SelectedProfile, AgentModelCapabilityKinds.Chat);
        _ = RefreshChatProviderSelectionAsync(value?.Id, chatBinding?.ModelId, ++_chatLoadVersion);
    }

    partial void OnSelectedChatModelChanged(ModelOption? value)
    {
        OnPropertyChanged(nameof(ShowReasoningOptions));
        if (_suppressSelectionHandlers || SelectedProfile is null)
        {
            return;
        }

        ApplyReasoningOptions(value?.Variants, selectedVariantId: null);
    }

    partial void OnSelectedEmbeddingProviderChanged(ProviderOption? value)
    {
        NotifyEmbeddingProviderStateChanged();
        if (_suppressSelectionHandlers || SelectedProfile is null)
        {
            return;
        }

        var embeddingBinding = FindModelBinding(
            SelectedProfile,
            AgentModelCapabilityKinds.Embedding
        );
        _ = RefreshEmbeddingProviderSelectionAsync(
            value?.Id,
            embeddingBinding?.ModelId,
            ++_embeddingLoadVersion
        );
    }

    [RelayCommand]
    private async Task CreateProfileAsync()
    {
        BeginBusy();
        try
        {
            var created = await _profileService.CreateProfileAsync("New Agent");
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
        if (SelectedProfile is null)
        {
            return;
        }

        BeginBusy();
        try
        {
            var profileId = SelectedProfile.ProfileId;
            _profileService.SaveProfile(
                profileId,
                string.IsNullOrWhiteSpace(DisplayName) ? "Unnamed Profile" : DisplayName.Trim(),
                string.IsNullOrWhiteSpace(Description) ? null : Description.Trim(),
                string.IsNullOrWhiteSpace(Instructions) ? null : Instructions.Trim(),
                SelectedChatProvider?.Id,
                SelectedChatModel?.Id,
                CanConfigureEmbeddings ? SelectedEmbeddingProvider?.Id : null,
                CanConfigureEmbeddings ? SelectedEmbeddingModel?.Id : null,
                selectableCapabilityAssignments: BuildSelectableCapabilityAssignments(),
                behaviorLoopId: SelectedBehaviorLoop?.LoopId ?? string.Empty,
                behaviorLoopSourceId: SelectedBehaviorLoop?.SourceId ?? string.Empty,
                behaviorLoopSettingsJson: SelectedProfile.BehaviorLoopSettingsJson ?? string.Empty,
                chatModelSettingsJson: BuildChatModelSettingsJson() ?? string.Empty
            );

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
            var deletedName = SelectedProfile.DisplayName;
            var shouldClearSelection = IsCompactLayout;
            _profileService.DeleteProfile(SelectedProfile.ProfileId);
            await ReloadProfilesAsync(selectProfileId: null);
            if (shouldClearSelection)
            {
                SelectedProfile = null;
                ClearStatus();
            }
            else
            {
                SetStatus(
                    $"Deleted profile '{deletedName}'.",
                    AgentProfileStatusKind.Success,
                    autoClear: true
                );
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

    [RelayCommand]
    private void BackToProfileList()
    {
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
            await RefreshProviderSectionsAsync(
                SelectedChatProvider?.Id,
                SelectedChatModel?.Id,
                SelectedReasoningOption?.VariantId,
                SelectedEmbeddingProvider?.Id,
                SelectedEmbeddingModel?.Id
            );
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
    private async Task OpenSelectedChatProviderSettingsAsync() =>
        await OpenProviderSettingsAsync(SelectedChatProvider?.PackageId);

    [RelayCommand]
    private async Task OpenSelectedEmbeddingProviderSettingsAsync() =>
        await OpenProviderSettingsAsync(SelectedEmbeddingProvider?.PackageId);

    [RelayCommand]
    private void OpenProfileEditor(AgentProfileRecord? profile)
    {
        if (profile is null)
        {
            return;
        }

        ActivateProfile(profile);
    }

    public void ActivateProfile(AgentProfileRecord profile)
    {
        if (
            !string.Equals(
                SelectedProfile?.ProfileId,
                profile.ProfileId,
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            SelectedProfile = profile;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    private async Task ReloadProfilesAsync(string? selectProfileId)
    {
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

                var selectedProfile = Profiles.FirstOrDefault(profile =>
                    profile.ProfileId == selectProfileId
                );
                if (selectedProfile is null && (!IsCompactLayout || selectProfileId is not null))
                {
                    selectedProfile =
                        Profiles.FirstOrDefault(profile => profile.ProfileId == currentProfileId)
                        ?? Profiles.FirstOrDefault();
                }

                SelectedProfile = selectedProfile;
            });

            if (SelectedProfile is null)
            {
                ClearEditor();
                if (Profiles.Count == 0)
                {
                    SetStatus("No profiles available.", AgentProfileStatusKind.Warning);
                }
                else
                {
                    ClearStatus();
                }

                return;
            }

            await LoadSelectedProfileAsync(SelectedProfile, ++_profileLoadVersion);
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task LoadSelectedProfileAsync(AgentProfileRecord? profile, int version)
    {
        if (profile is null)
        {
            ClearEditor();
            if (Profiles.Count == 0)
            {
                SetStatus("No profiles available.", AgentProfileStatusKind.Warning);
            }
            else
            {
                ClearStatus();
            }

            return;
        }

        BeginBusy();
        try
        {
            DisplayName = profile.DisplayName;
            Description = profile.Description ?? string.Empty;
            Instructions = profile.Instructions ?? string.Empty;
            var chatBinding = FindModelBinding(profile, AgentModelCapabilityKinds.Chat);
            var embeddingBinding = FindModelBinding(profile, AgentModelCapabilityKinds.Embedding);

            var localToolsTask = _profileService.ListInstalledLocalToolsAsync();
            var packageCapabilitiesTask = _profileService.ListSelectableProfileCapabilitiesAsync(
                profile
            );
            var behaviorLoops = _profileService
                .ListBehaviorLoops()
                .Select(loop => new BehaviorLoopOption(
                    loop.Descriptor.LoopId,
                    loop.Descriptor.SourceId,
                    loop.Descriptor.DisplayName,
                    loop.Descriptor.Description
                ))
                .ToArray();

            await Task.WhenAll(localToolsTask, packageCapabilitiesTask);

            if (!IsCurrentProfileLoad(version, profile.ProfileId))
            {
                return;
            }

            _chatLoadVersion++;
            _embeddingLoadVersion++;
            await RefreshProviderSectionsAsync(
                chatBinding?.ProviderId,
                chatBinding?.ModelId,
                AgentChatModelSettingsJson.Parse(chatBinding?.SettingsJson).ReasoningVariantId,
                embeddingBinding?.ProviderId,
                embeddingBinding?.ModelId
            );
            ApplyBehaviorLoopSelection(
                behaviorLoops,
                profile.BehaviorLoopId,
                profile.BehaviorLoopSourceId
            );
            ApplyCapabilityOptions(profile, await localToolsTask, await packageCapabilitiesTask);
        }
        catch (Exception ex)
        {
            if (IsCurrentProfileLoad(version, profile.ProfileId))
            {
                SetStatus(ex.Message, AgentProfileStatusKind.Error);
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private void OnSelectableCapabilitiesChanged() =>
        RunOnUiThread(() => _ = RefreshSelectedProfileCapabilitiesAsync());

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
            var localToolsTask = _profileService.ListInstalledLocalToolsAsync();
            var packageCapabilitiesTask = _profileService.ListSelectableProfileCapabilitiesAsync(
                profile
            );
            await Task.WhenAll(localToolsTask, packageCapabilitiesTask);
            if (!IsCurrentProfileLoad(version, profile.ProfileId))
            {
                return;
            }

            ApplyCapabilityOptions(profile, await localToolsTask, await packageCapabilitiesTask);
        }
        catch (Exception ex)
        {
            if (IsCurrentProfileLoad(version, profile.ProfileId))
            {
                SetStatus(ex.Message, AgentProfileStatusKind.Error);
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task RefreshProviderSectionsAsync(
        string? selectedChatProviderId,
        string? selectedChatModelId,
        string? selectedReasoningVariantId,
        string? selectedEmbeddingProviderId,
        string? selectedEmbeddingModelId
    )
    {
        var chatProviders = _profileService
            .ListChatProviders()
            .Select(provider => new ProviderOption(
                provider.Descriptor.ProviderId,
                provider.Descriptor.DisplayName,
                provider.Descriptor.PackageId
            ))
            .ToArray();
        var embeddingProviders = _profileService
            .ListEmbeddingProviders()
            .Select(provider => new ProviderOption(
                provider.Descriptor.ProviderId,
                provider.Descriptor.DisplayName,
                provider.Descriptor.PackageId
            ))
            .ToArray();
        var hasEmbeddingConsumers = _profileService.HasProfileCapabilityConsumers(
            AgentModelCapabilityKinds.Embedding
        );
        var effectiveChatProviderId = ResolveProviderId(
            chatProviders,
            selectedChatProviderId,
            selectFirstProvider: true
        );
        var effectiveEmbeddingProviderId = ResolveProviderId(
            embeddingProviders,
            selectedEmbeddingProviderId,
            selectFirstProvider: false
        );

        var chatModelsTask = _profileService.ListChatModelsAsync(effectiveChatProviderId);
        var chatReadinessTask = _profileService.GetChatProviderReadinessAsync(
            effectiveChatProviderId
        );
        var embeddingModelsTask = _profileService.ListEmbeddingModelsAsync(
            effectiveEmbeddingProviderId
        );
        var embeddingReadinessTask = _profileService.GetEmbeddingProviderReadinessAsync(
            effectiveEmbeddingProviderId
        );

        await Task.WhenAll(
            chatModelsTask,
            chatReadinessTask,
            embeddingModelsTask,
            embeddingReadinessTask
        );

        HasEmbeddingConsumers = hasEmbeddingConsumers;
        ApplyChatProviderSelection(
            chatProviders,
            effectiveChatProviderId,
            await chatModelsTask,
            selectedChatModelId
        );
        ApplyReasoningOptions(SelectedChatModel?.Variants, selectedReasoningVariantId);
        var chatReadiness = await chatReadinessTask;
        ApplyChatProviderReadiness(chatReadiness);
        ChatProviderStatusText = FormatChatProviderStatus(chatReadiness);

        ApplyEmbeddingProviderSelection(
            embeddingProviders,
            effectiveEmbeddingProviderId,
            await embeddingModelsTask,
            selectedEmbeddingModelId
        );
        var embeddingReadiness = await embeddingReadinessTask;
        ApplyEmbeddingProviderReadiness(embeddingReadiness, effectiveEmbeddingProviderId);
        EmbeddingProviderStatusText = FormatEmbeddingProviderStatus(
            embeddingReadiness,
            effectiveEmbeddingProviderId
        );
    }

    private async Task RefreshChatProviderSelectionAsync(
        string? providerId,
        string? selectedModelId,
        int version
    )
    {
        ChatModels.Clear();
        SelectedChatModel = null;
        ApplyReasoningOptions(null, selectedVariantId: null);

        if (string.IsNullOrWhiteSpace(providerId))
        {
            ChatProviderStatusText = "No chat provider selected.";
            ApplyChatProviderReadiness(null);
            return;
        }

        BeginBusy();
        try
        {
            ChatProviderStatusText = "Loading chat provider status...";

            var modelsTask = _profileService.ListChatModelsAsync(providerId);
            var readinessTask = _profileService.GetChatProviderReadinessAsync(providerId);

            await Task.WhenAll(modelsTask, readinessTask);
            if (!IsCurrentChatLoad(version, providerId))
            {
                return;
            }

            ApplyChatModels(await modelsTask, selectedModelId);
            ApplyReasoningOptions(SelectedChatModel?.Variants, selectedVariantId: null);
            var readiness = await readinessTask;
            ApplyChatProviderReadiness(readiness);
            ChatProviderStatusText = FormatChatProviderStatus(readiness);
        }
        catch (Exception ex)
        {
            if (IsCurrentChatLoad(version, providerId))
            {
                ChatModels.Clear();
                SelectedChatModel = null;
                ChatProviderStatusText = $"Chat provider status: Failed - {ex.Message}";
                SetChatProviderWarning($"Chat provider status could not be loaded: {ex.Message}");
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private async Task RefreshEmbeddingProviderSelectionAsync(
        string? providerId,
        string? selectedModelId,
        int version
    )
    {
        EmbeddingModels.Clear();
        SelectedEmbeddingModel = null;
        OnPropertyChanged(nameof(CanSelectEmbeddingModel));

        if (!HasEmbeddingConsumers)
        {
            EmbeddingProviderStatusText = "No installed profile feature consumes embeddings.";
            ApplyEmbeddingProviderReadiness(null, providerId);
            return;
        }

        if (!HasEmbeddingProviders)
        {
            EmbeddingProviderStatusText = "No embedding providers are installed.";
            ApplyEmbeddingProviderReadiness(null, providerId);
            return;
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            EmbeddingProviderStatusText = "Embeddings are disabled.";
            ApplyEmbeddingProviderReadiness(null, providerId);
            return;
        }

        BeginBusy();
        try
        {
            EmbeddingProviderStatusText = "Loading embedding provider status...";

            var modelsTask = _profileService.ListEmbeddingModelsAsync(providerId);
            var readinessTask = _profileService.GetEmbeddingProviderReadinessAsync(providerId);

            await Task.WhenAll(modelsTask, readinessTask);
            if (!IsCurrentEmbeddingLoad(version, providerId))
            {
                return;
            }

            ApplyEmbeddingModels(await modelsTask, selectedModelId);
            var readiness = await readinessTask;
            ApplyEmbeddingProviderReadiness(readiness, providerId);
            EmbeddingProviderStatusText = FormatEmbeddingProviderStatus(readiness, providerId);
        }
        catch (Exception ex)
        {
            if (IsCurrentEmbeddingLoad(version, providerId))
            {
                EmbeddingModels.Clear();
                SelectedEmbeddingModel = null;
                EmbeddingProviderStatusText = $"Embedding provider status: Failed - {ex.Message}";
                SetEmbeddingProviderWarning(
                    $"Embedding provider status could not be loaded: {ex.Message}"
                );
                OnPropertyChanged(nameof(CanSelectEmbeddingModel));
            }
        }
        finally
        {
            EndBusy();
        }
    }

    private void ClearEditor()
    {
        DisplayName = string.Empty;
        Description = string.Empty;
        Instructions = string.Empty;
        ChatProviders.Clear();
        ChatModels.Clear();
        ReasoningOptions.Clear();
        EmbeddingProviders.Clear();
        EmbeddingModels.Clear();
        BehaviorLoops.Clear();
        LocalTools.Clear();
        PackageCapabilities.Clear();
        LocalToolGroups.Clear();
        PackageCapabilityGroups.Clear();
        CapabilityGroups.Clear();
        _loadedCapabilityProfileId = null;
        _preservedSelectableCapabilityAssignments = [];
        SelectedChatProvider = null;
        SelectedChatModel = null;
        SelectedReasoningOption = null;
        SelectedEmbeddingProvider = null;
        SelectedEmbeddingModel = null;
        SelectedBehaviorLoop = null;
        ChatProviderStatusText = "No chat provider selected.";
        EmbeddingProviderStatusText = "Embeddings are disabled.";
        HasChatProviders = false;
        ClearChatProviderWarning();
        HasEmbeddingProviders = false;
        ClearEmbeddingProviderWarning();
        HasEmbeddingConsumers = false;
        HasLocalTools = false;
        HasPackageCapabilities = false;
        ToolSelectionSummary = "No local tools are enabled for this profile.";
        PackageCapabilitySelectionSummary = "No package capabilities are enabled for this profile.";
        OnPropertyChanged(nameof(HasReasoningOptions));
        OnPropertyChanged(nameof(ShowReasoningOptions));
        OnPropertyChanged(nameof(CanSelectEmbeddingModel));
    }

    private void ApplyBehaviorLoopSelection(
        IReadOnlyList<BehaviorLoopOption> availableLoops,
        string? selectedLoopId,
        string? selectedSourceId
    )
    {
        BehaviorLoops.Clear();
        foreach (var loop in availableLoops)
        {
            BehaviorLoops.Add(loop);
        }

        SetSelectionSilently(
            () =>
                SelectedBehaviorLoop =
                    BehaviorLoops.FirstOrDefault(option =>
                        IsBehaviorLoopMatch(option, selectedLoopId, selectedSourceId)
                    )
                    ?? BehaviorLoops.FirstOrDefault(option =>
                        string.Equals(option.LoopId, "default", StringComparison.OrdinalIgnoreCase)
                    )
                    ?? BehaviorLoops.FirstOrDefault()
        );
    }

    private static bool IsBehaviorLoopMatch(
        BehaviorLoopOption option,
        string? loopId,
        string? sourceId
    ) =>
        !string.IsNullOrWhiteSpace(loopId)
        && string.Equals(option.LoopId, loopId, StringComparison.OrdinalIgnoreCase)
        && (
            string.IsNullOrWhiteSpace(sourceId)
            || string.Equals(option.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)
        );

    private void ApplyChatProviderSelection(
        IReadOnlyList<ProviderOption> availableProviders,
        string? selectedProviderId,
        IReadOnlyList<AgentModelDescriptor> availableModels,
        string? selectedModelId
    )
    {
        HasChatProviders = availableProviders.Count > 0;
        ChatProviders.Clear();
        foreach (var provider in availableProviders)
        {
            ChatProviders.Add(provider);
        }

        if (!HasChatProviders)
        {
            SetSelectionSilently(() => SelectedChatProvider = null);
            ApplyChatModels([], selectedModelId: null);
            ClearChatProviderWarning();
            ChatProviderStatusText = "No chat providers are installed.";
            NotifyChatProviderStateChanged();
            return;
        }

        SetSelectionSilently(
            () =>
                SelectedChatProvider =
                    ChatProviders.FirstOrDefault(option => option.Id == selectedProviderId)
                    ?? ChatProviders.FirstOrDefault()
        );
        ApplyChatModels(availableModels, selectedModelId);
        NotifyChatProviderStateChanged();
    }

    private void ApplyChatModels(
        IReadOnlyList<AgentModelDescriptor> availableModels,
        string? selectedModelId
    )
    {
        ChatModels.Clear();
        foreach (
            var model in availableModels.Select(model => new ModelOption(
                model.ModelId,
                model.DisplayName,
                model.Variants
            ))
        )
        {
            ChatModels.Add(model);
        }

        SetSelectionSilently(
            () =>
                SelectedChatModel =
                    ChatModels.FirstOrDefault(option => option.Id == selectedModelId)
                    ?? ChatModels.FirstOrDefault()
        );
        OnPropertyChanged(nameof(ShowChatModelSelection));
        OnPropertyChanged(nameof(ShowReasoningOptions));
    }

    private void ApplyReasoningOptions(
        IReadOnlyList<AgentModelVariantDescriptor>? variants,
        string? selectedVariantId
    )
    {
        ReasoningOptions.Clear();
        SetSelectionSilently(() => SelectedReasoningOption = null);

        if (variants is null || variants.Count == 0)
        {
            OnPropertyChanged(nameof(HasReasoningOptions));
            OnPropertyChanged(nameof(ShowReasoningOptions));
            return;
        }

        ReasoningOptions.Add(
            new ModelReasoningOption(
                null,
                "Default",
                "Use the provider default reasoning behavior."
            )
        );
        foreach (
            var variant in variants.Where(variant => !string.IsNullOrWhiteSpace(variant.VariantId))
        )
        {
            ReasoningOptions.Add(
                new ModelReasoningOption(
                    variant.VariantId,
                    variant.DisplayName,
                    variant.Description
                )
            );
        }

        SetSelectionSilently(
            () =>
                SelectedReasoningOption =
                    ReasoningOptions.FirstOrDefault(option =>
                        !string.IsNullOrWhiteSpace(selectedVariantId)
                        && string.Equals(
                            option.VariantId,
                            selectedVariantId,
                            StringComparison.OrdinalIgnoreCase
                        )
                    ) ?? ReasoningOptions.FirstOrDefault()
        );
        OnPropertyChanged(nameof(HasReasoningOptions));
        OnPropertyChanged(nameof(ShowReasoningOptions));
    }

    private void ApplyEmbeddingProviderSelection(
        IReadOnlyList<ProviderOption> availableProviders,
        string? selectedProviderId,
        IReadOnlyList<AgentEmbeddingModelDescriptor> availableModels,
        string? selectedModelId
    )
    {
        HasEmbeddingProviders = availableProviders.Count > 0;

        EmbeddingProviders.Clear();
        EmbeddingModels.Clear();
        SetSelectionSilently(() =>
        {
            SelectedEmbeddingProvider = null;
            SelectedEmbeddingModel = null;
        });

        if (!HasEmbeddingConsumers)
        {
            EmbeddingProviderStatusText = "No installed profile feature consumes embeddings.";
            ClearEmbeddingProviderWarning();
            OnPropertyChanged(nameof(CanSelectEmbeddingModel));
            OnPropertyChanged(nameof(ShowEmbeddingModelSelection));
            return;
        }

        if (!HasEmbeddingProviders)
        {
            EmbeddingProviderStatusText = "No embedding providers are installed.";
            ClearEmbeddingProviderWarning();
            OnPropertyChanged(nameof(CanSelectEmbeddingModel));
            OnPropertyChanged(nameof(ShowEmbeddingModelSelection));
            return;
        }

        EmbeddingProviders.Add(new ProviderOption(null, "Disabled (text and metadata only)"));
        foreach (var provider in availableProviders)
        {
            EmbeddingProviders.Add(provider);
        }

        SetSelectionSilently(
            () =>
                SelectedEmbeddingProvider =
                    EmbeddingProviders.FirstOrDefault(option => option.Id == selectedProviderId)
                    ?? EmbeddingProviders.FirstOrDefault()
        );
        ApplyEmbeddingModels(availableModels, selectedModelId);
        NotifyEmbeddingProviderStateChanged();
    }

    private void ApplyEmbeddingModels(
        IReadOnlyList<AgentEmbeddingModelDescriptor> availableModels,
        string? selectedModelId
    )
    {
        EmbeddingModels.Clear();
        foreach (
            var model in availableModels.Select(model => new ModelOption(
                model.ModelId,
                model.DisplayName
            ))
        )
        {
            EmbeddingModels.Add(model);
        }

        SetSelectionSilently(
            () =>
                SelectedEmbeddingModel =
                    EmbeddingModels.FirstOrDefault(option => option.Id == selectedModelId)
                    ?? EmbeddingModels.FirstOrDefault()
        );
        OnPropertyChanged(nameof(CanSelectEmbeddingModel));
        OnPropertyChanged(nameof(ShowEmbeddingModelSelection));
    }

    private void ApplyCapabilityOptions(
        AgentProfileRecord profile,
        IReadOnlyList<AgentToolCatalogEntry> installedLocalTools,
        IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> packageCapabilities
    )
    {
        var assignments = GetEffectiveSelectableCapabilityAssignments(profile);
        var enabledToolAssignments = assignments
            .Where(assignment =>
                string.Equals(
                    assignment.Kind,
                    AgentProfileSelectableCapabilityKinds.Tool,
                    StringComparison.OrdinalIgnoreCase
                )
            )
            .ToArray();
        var localToolOptions = installedLocalTools
            .Select(item =>
            {
                var group = ResolveToolGroup(item.Descriptor);
                return new ProfileCapabilityOptionViewModel(
                    AgentProfileSelectableCapabilityKinds.Tool,
                    item.Descriptor.ToolId,
                    item.Descriptor.SourceId,
                    item.Descriptor.DisplayName,
                    item.Descriptor.Description,
                    string.Empty,
                    IsToolEnabled(enabledToolAssignments, item.Descriptor),
                    true,
                    group.Key,
                    group.Title,
                    group.Description,
                    group.SortOrder
                );
            })
            .ToArray();
        ReconcileCapabilityOptions(LocalTools, localToolOptions);
        ReconcileCapabilityGroups(LocalToolGroups, LocalTools);

        var packageCapabilityOptions = packageCapabilities
            .Select(capability =>
            {
                var group = ResolvePackageCapabilityGroup(capability);
                return new ProfileCapabilityOptionViewModel(
                    capability.Kind,
                    capability.CapabilityId,
                    capability.SourceId,
                    capability.DisplayName,
                    capability.Description,
                    capability.StatusText ?? string.Empty,
                    capability.IsSelectable
                        && IsCapabilityEnabled(
                            assignments,
                            capability.Kind,
                            capability.CapabilityId,
                            capability.SourceId
                        ),
                    capability.IsSelectable,
                    group.Key,
                    group.Title,
                    group.Description,
                    group.SortOrder
                );
            })
            .ToArray();
        ReconcileCapabilityOptions(PackageCapabilities, packageCapabilityOptions);
        ReconcileCapabilityGroups(PackageCapabilityGroups, PackageCapabilities);
        ReconcileCapabilityGroups(CapabilityGroups, LocalTools.Concat(PackageCapabilities));

        _preservedSelectableCapabilityAssignments = assignments
            .Where(assignment => !IsRenderedCapabilityAssignment(assignment))
            .ToArray();

        HasLocalTools = LocalTools.Count > 0;
        HasPackageCapabilities = PackageCapabilities.Count > 0;
        _loadedCapabilityProfileId = profile.ProfileId;
        RefreshCapabilitySummaries();
    }

    private IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> GetEffectiveSelectableCapabilityAssignments(
        AgentProfileRecord profile
    )
    {
        var assignments = new List<AgentProfileSelectableCapabilityAssignmentRecord>(
            GetSelectableCapabilityAssignments(profile)
        );
        if (string.Equals(_loadedCapabilityProfileId, profile.ProfileId, StringComparison.Ordinal))
        {
            assignments.AddRange(
                LocalTools
                    .Where(tool => tool.IsEnabled && tool.CanSelect)
                    .Select(tool => new AgentProfileSelectableCapabilityAssignmentRecord(
                        tool.Kind,
                        tool.CapabilityId,
                        tool.SourceId
                    ))
            );
            assignments.AddRange(
                PackageCapabilities
                    .Where(capability => capability.IsEnabled && capability.CanSelect)
                    .Select(capability => new AgentProfileSelectableCapabilityAssignmentRecord(
                        capability.Kind,
                        capability.CapabilityId,
                        capability.SourceId
                    ))
            );
        }

        return assignments.Distinct().ToArray();
    }

    private void ReconcileCapabilityOptions(
        ObservableCollection<ProfileCapabilityOptionViewModel> target,
        IEnumerable<ProfileCapabilityOptionViewModel> desiredItems
    )
    {
        target.Clear();
        foreach (
            var item in desiredItems.OrderBy(
                item => item.DisplayName,
                StringComparer.OrdinalIgnoreCase
            )
        )
        {
            item.SelectionChanged += OnCapabilitySelectionChanged;
            target.Add(item);
        }
    }

    private static void ReconcileCapabilityGroups(
        ObservableCollection<ProfileCapabilityGroupViewModel> target,
        IEnumerable<ProfileCapabilityOptionViewModel> options
    )
    {
        target.Clear();
        foreach (
            var group in options
                .GroupBy(option => option.GroupKey, StringComparer.OrdinalIgnoreCase)
                .Select(group => new ProfileCapabilityGroupViewModel(
                    group.First().GroupTitle,
                    group.First().GroupDescription,
                    group.First().GroupSortOrder,
                    group
                        .OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase)
                        .ToArray()
                ))
                .OrderBy(group => group.SortOrder)
                .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase)
        )
        {
            target.Add(group);
        }
    }

    private static CapabilityGroupInfo ResolveToolGroup(AgentToolDescriptor descriptor)
    {
        var title = FirstNonEmpty(
            descriptor.SelectionGroupDisplayName,
            descriptor.SourceDisplayName,
            HumanizeIdentifier(descriptor.SelectionGroupId),
            HumanizeIdentifier(descriptor.SourceId),
            "Tools"
        )!;
        var key = FirstNonEmpty(
            descriptor.SelectionGroupId,
            descriptor.SourceId,
            descriptor.SourceKind,
            title
        )!;
        return new CapabilityGroupInfo(
            "tool:" + key,
            title,
            descriptor.SelectionGroupDescription,
            10
        );
    }

    private static CapabilityGroupInfo ResolvePackageCapabilityGroup(
        AgentProfileSelectableCapabilityDescriptor capability
    )
    {
        var title = FirstNonEmpty(
            capability.GroupDisplayName,
            capability.SourceDisplayName,
            HumanizeIdentifier(capability.GroupId),
            HumanizeIdentifier(capability.SourceId),
            HumanizeIdentifier(capability.Kind),
            "Package Capabilities"
        )!;
        var key = FirstNonEmpty(capability.GroupId, capability.SourceId, capability.Kind, title)!;
        return new CapabilityGroupInfo(
            "package:" + key,
            title,
            capability.GroupDescription,
            capability.GroupSortOrder
        );
    }

    private static string? FirstNonEmpty(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? HumanizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(
            " ",
            value
                .Split(
                    ['-', '_', '.'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries
                )
                .Select(part =>
                    string.IsNullOrEmpty(part) ? part : char.ToUpperInvariant(part[0]) + part[1..]
                )
        );
    }

    private static bool IsToolEnabled(
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> enabledToolAssignments,
        AgentToolDescriptor descriptor
    ) =>
        enabledToolAssignments.Any(assignment =>
            IsToolAssignmentMatch(assignment.CapabilityId, descriptor)
            && IsSourceAssignmentMatch(assignment.SourceId, descriptor)
        );

    private static bool IsCapabilityEnabled(
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> assignments,
        string kind,
        string capabilityId,
        string? sourceId
    ) =>
        assignments.Any(assignment =>
            string.Equals(assignment.Kind, kind, StringComparison.OrdinalIgnoreCase)
            && string.Equals(
                assignment.CapabilityId,
                capabilityId,
                StringComparison.OrdinalIgnoreCase
            )
            && IsSelectableSourceAssignmentMatch(assignment.SourceId, sourceId)
        );

    private bool IsRenderedCapabilityAssignment(
        AgentProfileSelectableCapabilityAssignmentRecord assignment
    ) =>
        LocalTools.Any(item => IsCapabilityAssignmentMatch(assignment, item))
        || PackageCapabilities.Any(item => IsCapabilityAssignmentMatch(assignment, item));

    private static bool IsCapabilityAssignmentMatch(
        AgentProfileSelectableCapabilityAssignmentRecord assignment,
        ProfileCapabilityOptionViewModel item
    ) =>
        string.Equals(assignment.Kind, item.Kind, StringComparison.OrdinalIgnoreCase)
        && string.Equals(
            assignment.CapabilityId,
            item.CapabilityId,
            StringComparison.OrdinalIgnoreCase
        )
        && (
            string.IsNullOrWhiteSpace(assignment.SourceId)
            || IsSelectableSourceAssignmentMatch(assignment.SourceId, item.SourceId)
        );

    private static bool IsToolAssignmentMatch(
        string assignmentToolId,
        AgentToolDescriptor descriptor
    ) =>
        string.Equals(assignmentToolId, descriptor.ToolId, StringComparison.OrdinalIgnoreCase)
        || (
            descriptor.Aliases?.Any(alias =>
                string.Equals(assignmentToolId, alias, StringComparison.OrdinalIgnoreCase)
            ) ?? false
        );

    private static bool IsSourceAssignmentMatch(
        string? assignmentSourceId,
        AgentToolDescriptor descriptor
    ) =>
        string.IsNullOrWhiteSpace(assignmentSourceId)
        || (
            !string.IsNullOrWhiteSpace(descriptor.SourceId)
            && string.Equals(
                assignmentSourceId,
                descriptor.SourceId,
                StringComparison.OrdinalIgnoreCase
            )
        );

    private static bool IsSelectableSourceAssignmentMatch(
        string? assignmentSourceId,
        string? sourceId
    ) =>
        string.IsNullOrWhiteSpace(assignmentSourceId)
            ? string.IsNullOrWhiteSpace(sourceId)
            : !string.IsNullOrWhiteSpace(sourceId)
                && string.Equals(assignmentSourceId, sourceId, StringComparison.OrdinalIgnoreCase);

    private void OnCapabilitySelectionChanged()
    {
        RefreshCapabilitySummaries();
    }

    private void RefreshCapabilitySummaries()
    {
        var enabledTools = LocalTools
            .Where(tool => tool.IsEnabled && tool.CanSelect)
            .Select(tool => tool.DisplayName)
            .ToArray();
        ToolSelectionSummary =
            enabledTools.Length == 0
                ? "No local tools are enabled for this profile."
                : $"Enabled local tools: {string.Join(", ", enabledTools)}";

        var enabledPackageCapabilities = PackageCapabilities
            .Where(capability => capability.IsEnabled && capability.CanSelect)
            .Select(capability => capability.DisplayName)
            .ToArray();
        PackageCapabilitySelectionSummary =
            enabledPackageCapabilities.Length == 0
                ? "No package capabilities are enabled for this profile."
                : $"Enabled package capabilities: {string.Join(", ", enabledPackageCapabilities)}";
    }

    private IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> BuildSelectableCapabilityAssignments()
    {
        var assignments = new List<AgentProfileSelectableCapabilityAssignmentRecord>(
            _preservedSelectableCapabilityAssignments
        );
        assignments.AddRange(
            LocalTools
                .Where(tool => tool.IsEnabled && tool.CanSelect)
                .Select(tool => new AgentProfileSelectableCapabilityAssignmentRecord(
                    tool.Kind,
                    tool.CapabilityId,
                    tool.SourceId
                ))
        );
        assignments.AddRange(
            PackageCapabilities
                .Where(capability => capability.IsEnabled && capability.CanSelect)
                .Select(capability => new AgentProfileSelectableCapabilityAssignmentRecord(
                    capability.Kind,
                    capability.CapabilityId,
                    capability.SourceId
                ))
        );
        return assignments.Distinct().ToArray();
    }

    private void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSuccessStatusClear();
        _profileService.SelectableCapabilitiesChanged -= OnSelectableCapabilitiesChanged;
    }

    private string? BuildChatModelSettingsJson() =>
        !HasReasoningOptions || string.IsNullOrWhiteSpace(SelectedReasoningOption?.VariantId)
            ? null
            : AgentChatModelSettingsJson.Serialize(
                new AgentChatModelSettings(SelectedReasoningOption.VariantId)
            );

    private static IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> GetSelectableCapabilityAssignments(
        AgentProfileRecord profile
    ) => profile.SelectableCapabilityAssignments ?? [];

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

    private bool IsCurrentProfileLoad(int version, string profileId) =>
        version == _profileLoadVersion
        && string.Equals(SelectedProfile?.ProfileId, profileId, StringComparison.Ordinal);

    private bool IsCurrentChatLoad(int version, string? providerId) =>
        version == _chatLoadVersion
        && string.Equals(SelectedChatProvider?.Id, providerId, StringComparison.OrdinalIgnoreCase);

    private bool IsCurrentEmbeddingLoad(int version, string? providerId) =>
        version == _embeddingLoadVersion
        && string.Equals(
            SelectedEmbeddingProvider?.Id,
            providerId,
            StringComparison.OrdinalIgnoreCase
        );

    private void ClearStatus() => SetStatus(string.Empty, AgentProfileStatusKind.None);

    private void SetStatus(string message, AgentProfileStatusKind kind, bool autoClear = false)
    {
        CancelSuccessStatusClear();
        StatusKind = string.IsNullOrWhiteSpace(message) ? AgentProfileStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == AgentProfileStatusKind.Success)
        {
            ScheduleSuccessStatusClear(message);
        }
    }

    private void ScheduleSuccessStatusClear(string message)
    {
        var cancellation = new CancellationTokenSource();
        _successStatusClearCancellation = cancellation;
        _ = ClearSuccessStatusAfterDelayAsync(message, cancellation);
    }

    private async Task ClearSuccessStatusAfterDelayAsync(
        string message,
        CancellationTokenSource cancellation
    )
    {
        try
        {
            await Task.Delay(SuccessStatusDisplayDuration, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        RunOnUiThread(() =>
        {
            if (
                _successStatusClearCancellation == cancellation
                && StatusKind == AgentProfileStatusKind.Success
                && string.Equals(StatusText, message, StringComparison.Ordinal)
            )
            {
                ClearStatus();
            }
        });
    }

    private void CancelSuccessStatusClear()
    {
        var cancellation = _successStatusClearCancellation;
        if (cancellation is null)
        {
            return;
        }

        _successStatusClearCancellation = null;
        cancellation.Cancel();
        cancellation.Dispose();
    }

    private async Task OpenProviderSettingsAsync(string? packageId)
    {
        if (_settingsNavigationService is null || string.IsNullOrWhiteSpace(packageId))
        {
            SetStatus(
                "Package settings cannot be opened from this host.",
                AgentProfileStatusKind.Warning
            );
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

    private void ApplyChatProviderReadiness(AgentProviderReadiness? readiness)
    {
        if (
            !HasChatProviders
            || SelectedChatProvider?.Id is null
            || readiness is null
            || readiness.Status == AgentProviderReadinessStatus.Ready
        )
        {
            ClearChatProviderWarning();
            return;
        }

        SetChatProviderWarning(readiness.Message);
    }

    private void ApplyEmbeddingProviderReadiness(
        AgentEmbeddingProviderReadiness? readiness,
        string? providerId
    )
    {
        if (
            !HasEmbeddingConsumers
            || !HasEmbeddingProviders
            || string.IsNullOrWhiteSpace(providerId)
            || readiness is null
            || readiness.Status == AgentProviderReadinessStatus.Ready
        )
        {
            ClearEmbeddingProviderWarning();
            return;
        }

        SetEmbeddingProviderWarning(readiness.Message);
    }

    private void SetChatProviderWarning(string message)
    {
        ChatProviderWarningText = message;
        HasChatProviderWarning = true;
        NotifyChatProviderStateChanged();
    }

    private void ClearChatProviderWarning()
    {
        ChatProviderWarningText = string.Empty;
        HasChatProviderWarning = false;
        NotifyChatProviderStateChanged();
    }

    private void SetEmbeddingProviderWarning(string message)
    {
        EmbeddingProviderWarningText = message;
        HasEmbeddingProviderWarning = true;
        NotifyEmbeddingProviderStateChanged();
    }

    private void ClearEmbeddingProviderWarning()
    {
        EmbeddingProviderWarningText = string.Empty;
        HasEmbeddingProviderWarning = false;
        NotifyEmbeddingProviderStateChanged();
    }

    private void NotifyChatProviderStateChanged()
    {
        OnPropertyChanged(nameof(HasNoChatProviders));
        OnPropertyChanged(nameof(ShowChatProviderPicker));
        OnPropertyChanged(nameof(ShowChatProviderWarning));
        OnPropertyChanged(nameof(ShowChatModelSelection));
        OnPropertyChanged(nameof(ShowReasoningOptions));
        OnPropertyChanged(nameof(CanOpenChatProviderSettings));
    }

    private void NotifyEmbeddingProviderStateChanged()
    {
        OnPropertyChanged(nameof(CanSelectEmbeddingModel));
        OnPropertyChanged(nameof(ShowEmbeddingProviderPicker));
        OnPropertyChanged(nameof(ShowEmbeddingProviderEmptyState));
        OnPropertyChanged(nameof(ShowEmbeddingProviderWarning));
        OnPropertyChanged(nameof(ShowEmbeddingModelSelection));
        OnPropertyChanged(nameof(CanOpenEmbeddingProviderSettings));
    }

    private static string? ResolveProviderId(
        IReadOnlyList<ProviderOption> providers,
        string? selectedProviderId,
        bool selectFirstProvider
    )
    {
        if (
            !string.IsNullOrWhiteSpace(selectedProviderId)
            && providers.Any(provider =>
                string.Equals(provider.Id, selectedProviderId, StringComparison.OrdinalIgnoreCase)
            )
        )
        {
            return selectedProviderId;
        }

        return selectFirstProvider ? providers.FirstOrDefault()?.Id : null;
    }

    private static string FormatChatProviderStatus(
        AgentProviderReadiness? readiness,
        string prefix = ""
    ) =>
        readiness is null
            ? prefix + "No chat provider selected."
            : prefix + $"Chat provider status: {readiness.Status} - {readiness.Message}";

    private string FormatEmbeddingProviderStatus(
        AgentEmbeddingProviderReadiness? readiness,
        string? providerId,
        string prefix = ""
    )
    {
        if (!HasEmbeddingConsumers)
        {
            return "No installed profile feature consumes embeddings.";
        }

        if (!HasEmbeddingProviders)
        {
            return "No embedding providers are installed.";
        }

        if (string.IsNullOrWhiteSpace(providerId))
        {
            return prefix + "Embeddings are disabled.";
        }

        return readiness is null
            ? prefix + "No embedding provider selected."
            : prefix + $"Embedding provider status: {readiness.Status} - {readiness.Message}";
    }

    private static AgentProfileModelBindingRecord? FindModelBinding(
        AgentProfileRecord profile,
        string capabilityKind
    )
    {
        var binding = profile.ModelBindings?.FirstOrDefault(candidate =>
            string.Equals(
                candidate.CapabilityKind,
                capabilityKind,
                StringComparison.OrdinalIgnoreCase
            )
        );
        if (binding is not null)
        {
            return binding;
        }

        return string.Equals(
                capabilityKind,
                AgentModelCapabilityKinds.Chat,
                StringComparison.OrdinalIgnoreCase
            )
                ? BuildModelBindingFromProfileFields(
                    profile.ProfileId,
                    capabilityKind,
                    profile.ChatProviderId,
                    profile.ChatModelId,
                    profile.UpdatedAtUtc
                )
            : string.Equals(
                capabilityKind,
                AgentModelCapabilityKinds.Embedding,
                StringComparison.OrdinalIgnoreCase
            )
                ? BuildModelBindingFromProfileFields(
                    profile.ProfileId,
                    capabilityKind,
                    profile.EmbeddingProviderId,
                    profile.EmbeddingModelId,
                    profile.UpdatedAtUtc
                )
            : null;
    }

    private static AgentProfileModelBindingRecord? BuildModelBindingFromProfileFields(
        string profileId,
        string capabilityKind,
        string? providerId,
        string? modelId,
        DateTimeOffset updatedAtUtc
    ) =>
        string.IsNullOrWhiteSpace(providerId) && string.IsNullOrWhiteSpace(modelId)
            ? null
            : new(profileId, capabilityKind, providerId, modelId, SettingsJson: null, updatedAtUtc);
}

public enum AgentProfileStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

public sealed record ProviderOption(string? Id, string Label, string? PackageId = null);

public sealed record ModelOption(
    string? Id,
    string Label,
    IReadOnlyList<AgentModelVariantDescriptor>? Variants = null
);

public sealed record ModelReasoningOption(
    string? VariantId,
    string Label,
    string? Description = null
)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

public sealed record BehaviorLoopOption(
    string LoopId,
    string? SourceId,
    string Label,
    string Description
);

public sealed record ProfileCapabilityGroupViewModel(
    string Title,
    string? Description,
    int SortOrder,
    IReadOnlyList<ProfileCapabilityOptionViewModel> Options
)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed record CapabilityGroupInfo(
    string Key,
    string Title,
    string? Description,
    int SortOrder
);

public sealed partial class ProfileCapabilityOptionViewModel : ObservableObject
{
    public ProfileCapabilityOptionViewModel(
        string kind,
        string capabilityId,
        string? sourceId,
        string displayName,
        string? description,
        string statusText,
        bool isEnabled,
        bool canSelect = true,
        string? groupKey = null,
        string? groupTitle = null,
        string? groupDescription = null,
        int groupSortOrder = 0
    )
    {
        Kind = kind;
        CapabilityId = capabilityId;
        SourceId = sourceId;
        DisplayName = displayName;
        Description = description;
        StatusText = statusText;
        CanSelect = canSelect;
        GroupKey = string.IsNullOrWhiteSpace(groupKey) ? kind : groupKey.Trim();
        GroupTitle = string.IsNullOrWhiteSpace(groupTitle) ? kind : groupTitle.Trim();
        GroupDescription = string.IsNullOrWhiteSpace(groupDescription)
            ? null
            : groupDescription.Trim();
        GroupSortOrder = groupSortOrder;
        _isEnabled = canSelect && isEnabled;
    }

    public event Action? SelectionChanged;

    public string Kind { get; }

    public string CapabilityId { get; }

    public string? SourceId { get; }

    public string DisplayName { get; }

    public string? Description { get; }

    public string StatusText { get; }

    public bool CanSelect { get; }

    public string GroupKey { get; }

    public string GroupTitle { get; }

    public string? GroupDescription { get; }

    public int GroupSortOrder { get; }

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    [ObservableProperty]
    private bool _isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (!CanSelect && value)
        {
            IsEnabled = false;
            return;
        }

        SelectionChanged?.Invoke();
    }
}
