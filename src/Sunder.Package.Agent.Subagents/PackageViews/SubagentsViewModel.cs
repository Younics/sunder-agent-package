using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Contracts.Services;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.PackageViews;

public sealed partial class SubagentsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly SubagentService _subagentService;
    private readonly IPackageExtensionCatalog? _extensionCatalog;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private readonly AgentProfileSelectableCapabilityChangeObserver? _capabilityChangeObserver;
    private CancellationTokenSource? _successStatusClearCancellation;
    private bool _suppressSelectionHandlers;
    private bool _suppressChatProviderSelection;
    private bool _disposed;
    private string? _loadedCapabilitySubagentId;

    public SubagentsViewModel(
        SubagentService subagentService,
        IPackageExtensionCatalog extensionCatalog,
        IPackageSettingsNavigationService? settingsNavigationService = null)
    {
        _subagentService = subagentService;
        _extensionCatalog = extensionCatalog;
        _settingsNavigationService = settingsNavigationService;
        if (_extensionCatalog is not null)
        {
            _capabilityChangeObserver = new AgentProfileSelectableCapabilityChangeObserver(_extensionCatalog);
            _capabilityChangeObserver.Changed += OnSelectableCapabilitiesChanged;
        }
    }

    public ObservableCollection<SubagentRecord> Subagents { get; } = [];

    public ObservableCollection<SubagentProviderOption> ChatProviders { get; } = [];

    public ObservableCollection<SubagentModelOption> ChatModels { get; } = [];

    public ObservableCollection<SubagentReasoningOption> ReasoningOptions { get; } = [];

    public ObservableCollection<SubagentCapabilityOptionViewModel> CapabilityOptions { get; } = [];

    public ObservableCollection<SubagentCapabilityGroupViewModel> CapabilityGroups { get; } = [];

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

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
    private SubagentProviderOption? _selectedChatProvider;

    [ObservableProperty]
    private SubagentModelOption? _selectedChatModel;

    [ObservableProperty]
    private SubagentReasoningOption? _selectedReasoningOption;

    [ObservableProperty]
    private bool _hasChatProviderChoices;

    [ObservableProperty]
    private bool _hasChatProviderWarning;

    [ObservableProperty]
    private string _chatProviderWarningText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private SubagentStatusKind _statusKind = SubagentStatusKind.None;

    public bool HasSelectedSubagent => SelectedSubagent is not null;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsStatusSuccess => StatusKind == SubagentStatusKind.Success;

    public bool IsStatusWarning => StatusKind == SubagentStatusKind.Warning;

    public bool IsStatusError => StatusKind == SubagentStatusKind.Error;

    public bool CanSaveSelectedSubagent => SelectedSubagent is not null && !string.IsNullOrWhiteSpace(Description);

    public bool IsSelectedSubagentIncomplete => SelectedSubagent is not null && string.IsNullOrWhiteSpace(Description);

    public string DescriptionValidationText => IsSelectedSubagentIncomplete
        ? "Description is required before this subagent can be saved, selected, or used for delegation."
        : string.Empty;

    public bool HasSelectedChatProvider => !string.IsNullOrWhiteSpace(SelectedChatProvider?.ProviderId);

    public bool HasReasoningOptions => HasSelectedChatProvider && ReasoningOptions.Count > 0;

    public bool HasNoChatProviderChoices => !HasChatProviderChoices;

    public bool ShowChatProviderPicker => HasChatProviderChoices;

    public bool ShowChatProviderWarning => HasChatProviderWarning;

    public bool ShowChatModelSelection => HasSelectedChatProvider && !HasChatProviderWarning;

    public bool ShowReasoningOptions => ShowChatModelSelection && HasReasoningOptions;

    public bool CanOpenChatProviderSettings => ShowChatProviderWarning
                                               && _settingsNavigationService is not null
                                               && !string.IsNullOrWhiteSpace(SelectedChatProvider?.PackageId);

    public SubagentsViewModel() : this(null!, null!)
    {
    }

    partial void OnSelectedChatProviderChanged(SubagentProviderOption? value)
    {
        OnPropertyChanged(nameof(HasSelectedChatProvider));
        OnPropertyChanged(nameof(HasReasoningOptions));
        NotifyChatProviderStateChanged();
        if (_suppressChatProviderSelection)
        {
            return;
        }

        _ = LoadChatModelsAsync(value?.ProviderId, SelectedChatModel?.ModelId);
    }

    partial void OnSelectedChatModelChanged(SubagentModelOption? value)
    {
        OnPropertyChanged(nameof(ShowReasoningOptions));
        if (_suppressChatProviderSelection)
        {
            return;
        }

        ApplyReasoningOptions(value?.Variants, selectedVariantId: null);
    }

    partial void OnHasChatProviderChoicesChanged(bool value)
        => NotifyChatProviderStateChanged();

    partial void OnHasChatProviderWarningChanged(bool value)
        => NotifyChatProviderStateChanged();

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

    partial void OnSelectedSubagentChanged(SubagentRecord? value)
    {
        OnPropertyChanged(nameof(HasSelectedSubagent));
        OnPropertyChanged(nameof(CanSaveSelectedSubagent));
        OnPropertyChanged(nameof(IsSelectedSubagentIncomplete));
        OnPropertyChanged(nameof(DescriptionValidationText));
        SaveSubagentCommand.NotifyCanExecuteChanged();
        DeleteSubagentCommand.NotifyCanExecuteChanged();
        if (_suppressSelectionHandlers)
        {
            return;
        }

        _ = LoadSelectedSubagentAsync(value);
        if (IsCompactLayout && value is not null)
        {
            IsEditorActive = true;
        }
    }

    partial void OnDescriptionChanged(string value)
    {
        OnPropertyChanged(nameof(CanSaveSelectedSubagent));
        OnPropertyChanged(nameof(IsSelectedSubagentIncomplete));
        OnPropertyChanged(nameof(DescriptionValidationText));
        SaveSubagentCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand]
    private async Task CreateSubagentAsync()
    {
        var created = _subagentService.CreateSubagent("New Subagent");
        await ReloadAsync(created.SubagentId);
        IsEditorActive = true;
        ClearStatus();
    }

    [RelayCommand(CanExecute = nameof(CanSaveSubagent))]
    private async Task SaveSubagentAsync()
    {
        if (SelectedSubagent is null)
        {
            return;
        }

        try
        {
            var saved = _subagentService.SaveSubagent(
                SelectedSubagent.SubagentId,
                DisplayName,
                Description,
                Instructions,
                SelectedChatProvider?.ProviderId,
                SelectedChatModel?.ModelId,
                CapabilityOptions
                    .Where(option => option.IsEnabled)
                    .Select(option => new AgentProfileSelectableCapabilityAssignmentRecord(option.Kind, option.CapabilityId, option.SourceId))
                    .Distinct()
                    .ToArray(),
                BuildChatModelSettingsJson());
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
        if (SelectedSubagent is null)
        {
            return;
        }

        var deletedName = SelectedSubagent.DisplayName;
        var shouldClearSelection = IsCompactLayout;
        _subagentService.DeleteSubagent(SelectedSubagent.SubagentId);
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

    private bool CanEditSubagent() => SelectedSubagent is not null;

    private bool CanSaveSubagent() => CanSaveSelectedSubagent;

    [RelayCommand]
    private void BackToSubagentList()
    {
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
            await LoadChatProvidersAsync(
                SelectedChatProvider?.ProviderId,
                SelectedChatModel?.ModelId,
                BuildChatModelSettingsJson());
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SubagentStatusKind.Error);
        }
    }

    [RelayCommand]
    private async Task OpenSelectedChatProviderSettingsAsync()
        => await OpenProviderSettingsAsync(SelectedChatProvider?.PackageId);

    [RelayCommand]
    private void OpenSubagentEditor(SubagentRecord? subagent)
    {
        if (subagent is null)
        {
            return;
        }

        ActivateSubagent(subagent);
    }

    public void ActivateSubagent(SubagentRecord subagent)
    {
        if (!string.Equals(SelectedSubagent?.SubagentId, subagent.SubagentId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSubagent = subagent;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    public async Task InitializeAsync()
        => await ReloadAsync(null);

    private async Task ReloadAsync(string? selectedSubagentId)
    {
        var currentSubagentId = SelectedSubagent?.SubagentId;
        SetSelectionSilently(() =>
        {
            Subagents.Clear();
            foreach (var subagent in _subagentService.ListSubagents())
            {
                Subagents.Add(subagent);
            }

            var selectedSubagent = Subagents.FirstOrDefault(agent => string.Equals(agent.SubagentId, selectedSubagentId, StringComparison.OrdinalIgnoreCase));
            if (selectedSubagent is null && (!IsCompactLayout || selectedSubagentId is not null))
            {
                selectedSubagent = Subagents.FirstOrDefault(agent => string.Equals(agent.SubagentId, currentSubagentId, StringComparison.OrdinalIgnoreCase))
                                   ?? Subagents.FirstOrDefault();
            }

            SelectedSubagent = selectedSubagent;
        });
        if (SelectedSubagent is null)
        {
            ClearEditor();
            return;
        }

        await LoadSelectedSubagentAsync(SelectedSubagent);
    }

    private async Task LoadSelectedSubagentAsync(SubagentRecord? subagent)
    {
        if (subagent is null)
        {
            ClearEditor();
            return;
        }

        DisplayName = subagent.DisplayName;
        Description = subagent.Description ?? string.Empty;
        Instructions = subagent.Instructions ?? string.Empty;
        await LoadChatProvidersAsync(subagent.ChatProviderId, subagent.ChatModelId, subagent.ChatModelSettingsJson);
        await RefreshSelectedSubagentCapabilitiesAsync(subagent);
    }

    private async Task RefreshSelectedSubagentCapabilitiesAsync()
    {
        var subagent = SelectedSubagent;
        if (subagent is null)
        {
            return;
        }

        await RefreshSelectedSubagentCapabilitiesAsync(subagent);
    }

    private async Task RefreshSelectedSubagentCapabilitiesAsync(SubagentRecord subagent)
    {
        var localToolsTask = ListInstalledLocalToolsAsync();
        var packageCapabilitiesTask = ListSelectableProfileCapabilitiesAsync();
        await Task.WhenAll(localToolsTask, packageCapabilitiesTask);

        if (!string.Equals(SelectedSubagent?.SubagentId, subagent.SubagentId, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        ApplyCapabilityOptions(subagent, await localToolsTask, await packageCapabilitiesTask);
    }

    private void ApplyCapabilityOptions(
        SubagentRecord subagent,
        IReadOnlyList<AgentToolDescriptor> localTools,
        IReadOnlyList<AgentProfileSelectableCapabilityDescriptor> packageCapabilities)
    {
        var assignments = GetEffectiveCapabilityAssignments(subagent);

        var options = new List<SubagentCapabilityOptionViewModel>();
        options.AddRange(localTools.Select(tool =>
        {
            var group = ResolveToolGroup(tool);
            return new SubagentCapabilityOptionViewModel(
                AgentProfileSelectableCapabilityKinds.Tool,
                tool.ToolId,
                tool.SourceId,
                tool.DisplayName,
                tool.Description,
                IsEnabled(assignments, AgentProfileSelectableCapabilityKinds.Tool, tool.ToolId, tool.SourceId),
                group.Key,
                group.Title,
                group.Description,
                group.SortOrder);
        }));
        options.AddRange(packageCapabilities
            .Where(capability => !string.Equals(capability.Kind, AgentProfileSelectableCapabilityKinds.Subagent, StringComparison.OrdinalIgnoreCase)
                                 || !string.Equals(capability.SourceId, SubagentConstants.PackageId, StringComparison.OrdinalIgnoreCase))
            .Select(capability =>
            {
                var group = ResolvePackageCapabilityGroup(capability);
                return new SubagentCapabilityOptionViewModel(
                    capability.Kind,
                    capability.CapabilityId,
                    capability.SourceId,
                    capability.DisplayName,
                    capability.Description,
                    IsEnabled(assignments, capability.Kind, capability.CapabilityId, capability.SourceId),
                    group.Key,
                    group.Title,
                    group.Description,
                    group.SortOrder);
            }));

        CapabilityOptions.Clear();
        foreach (var option in options.OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase))
        {
            CapabilityOptions.Add(option);
        }

        ReconcileCapabilityGroups();
        _loadedCapabilitySubagentId = subagent.SubagentId;
    }

    private void ClearEditor()
    {
        DisplayName = string.Empty;
        Description = string.Empty;
        Instructions = string.Empty;
        ChatProviders.Clear();
        ChatModels.Clear();
        ReasoningOptions.Clear();
        HasChatProviderChoices = false;
        ClearChatProviderWarning();
        _suppressChatProviderSelection = true;
        try
        {
            SelectedChatProvider = null;
            SelectedChatModel = null;
            SelectedReasoningOption = null;
        }
        finally
        {
            _suppressChatProviderSelection = false;
        }

        OnPropertyChanged(nameof(HasSelectedChatProvider));
        OnPropertyChanged(nameof(HasReasoningOptions));
        OnPropertyChanged(nameof(ShowReasoningOptions));
        CapabilityOptions.Clear();
        CapabilityGroups.Clear();
        _loadedCapabilitySubagentId = null;
    }

    private void ReconcileCapabilityGroups()
    {
        CapabilityGroups.Clear();
        foreach (var group in CapabilityOptions
                     .GroupBy(option => option.GroupKey, StringComparer.OrdinalIgnoreCase)
                     .Select(group => new SubagentCapabilityGroupViewModel(
                         group.First().GroupTitle,
                         group.First().GroupDescription,
                         group.First().GroupSortOrder,
                         group.OrderBy(option => option.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray()))
                     .OrderBy(group => group.SortOrder)
                     .ThenBy(group => group.Title, StringComparer.OrdinalIgnoreCase))
        {
            CapabilityGroups.Add(group);
        }
    }

    private async Task LoadChatProvidersAsync(string? selectedProviderId, string? selectedModelId, string? selectedSettingsJson)
    {
        ChatProviders.Clear();
        IReadOnlyList<IAgentChatProvider> providers = _extensionCatalog is null
            ? []
            : _extensionCatalog.GetExtensions(PackageExtensionPoints.ChatProviders)
                .OrderBy(provider => provider.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        HasChatProviderChoices = providers.Count > 0;

        if (!HasChatProviderChoices)
        {
            _suppressChatProviderSelection = true;
            try
            {
                SelectedChatProvider = null;
                SelectedChatModel = null;
                SelectedReasoningOption = null;
            }
            finally
            {
                _suppressChatProviderSelection = false;
            }

            ChatModels.Clear();
            ReasoningOptions.Clear();
            ClearChatProviderWarning();
            NotifyChatProviderStateChanged();
            return;
        }

        ChatProviders.Add(new SubagentProviderOption(null, "Inherit parent chat model"));
        if (_extensionCatalog is not null)
        {
            foreach (var provider in providers)
            {
                ChatProviders.Add(new SubagentProviderOption(
                    provider.Descriptor.ProviderId,
                    provider.Descriptor.DisplayName,
                    provider.Descriptor.PackageId));
            }
        }

        _suppressChatProviderSelection = true;
        try
        {
            SelectedChatProvider = ChatProviders.FirstOrDefault(option => string.Equals(option.ProviderId, selectedProviderId, StringComparison.OrdinalIgnoreCase))
                                   ?? ChatProviders.FirstOrDefault();
        }
        finally
        {
            _suppressChatProviderSelection = false;
        }

        await LoadChatModelsAsync(SelectedChatProvider?.ProviderId, selectedModelId);
        ApplyReasoningOptions(
            SelectedChatModel?.Variants,
            AgentChatModelSettingsJson.Parse(selectedSettingsJson).ReasoningVariantId);
        NotifyChatProviderStateChanged();
    }

    private async Task LoadChatModelsAsync(string? providerId, string? selectedModelId)
    {
        ChatModels.Clear();
        SelectedChatModel = null;
        ApplyReasoningOptions(null, selectedVariantId: null);
        if (_extensionCatalog is null || string.IsNullOrWhiteSpace(providerId))
        {
            ClearChatProviderWarning();
            return;
        }

        var provider = _extensionCatalog.GetExtensions(PackageExtensionPoints.ChatProviders)
            .FirstOrDefault(provider => string.Equals(provider.Descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase));
        if (provider is null)
        {
            ClearChatProviderWarning();
            return;
        }

        try
        {
            var models = await provider.GetAvailableModelsAsync();
            var readiness = await provider.GetReadinessAsync();
            ApplyChatProviderReadiness(readiness);

            foreach (var model in models.OrderBy(model => model.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                ChatModels.Add(new SubagentModelOption(model.ModelId, model.DisplayName, model.Variants));
            }
        }
        catch (Exception ex)
        {
            SetChatProviderWarning($"Chat provider status could not be loaded: {ex.Message}");
        }

        SelectedChatModel = ChatModels.FirstOrDefault(option => string.Equals(option.ModelId, selectedModelId, StringComparison.OrdinalIgnoreCase))
                            ?? ChatModels.FirstOrDefault();
        ApplyReasoningOptions(SelectedChatModel?.Variants, selectedVariantId: null);
        NotifyChatProviderStateChanged();
    }

    private void ApplyReasoningOptions(IReadOnlyList<AgentModelVariantDescriptor>? variants, string? selectedVariantId)
    {
        ReasoningOptions.Clear();
        SelectedReasoningOption = null;

        if (!HasSelectedChatProvider || variants is null || variants.Count == 0)
        {
            OnPropertyChanged(nameof(HasReasoningOptions));
            OnPropertyChanged(nameof(ShowReasoningOptions));
            return;
        }

        ReasoningOptions.Add(new SubagentReasoningOption(null, "Default", "Use the selected model's default reasoning behavior."));
        foreach (var variant in variants.Where(variant => !string.IsNullOrWhiteSpace(variant.VariantId)))
        {
            ReasoningOptions.Add(new SubagentReasoningOption(variant.VariantId, variant.DisplayName, variant.Description));
        }

        SelectedReasoningOption = ReasoningOptions.FirstOrDefault(option =>
                                      !string.IsNullOrWhiteSpace(selectedVariantId)
                                      && string.Equals(option.VariantId, selectedVariantId, StringComparison.OrdinalIgnoreCase))
                                  ?? ReasoningOptions.FirstOrDefault();
        OnPropertyChanged(nameof(HasReasoningOptions));
        OnPropertyChanged(nameof(ShowReasoningOptions));
    }

    private string? BuildChatModelSettingsJson()
        => !HasReasoningOptions || string.IsNullOrWhiteSpace(SelectedReasoningOption?.VariantId)
            ? null
            : AgentChatModelSettingsJson.Serialize(new AgentChatModelSettings(SelectedReasoningOption.VariantId));

    private async Task<IReadOnlyList<AgentToolDescriptor>> ListInstalledLocalToolsAsync()
    {
        if (_extensionCatalog is null)
        {
            return [];
        }

        var context = new AgentToolSourceContext(SessionId: null, Profile: null, Workspace: null, ExecutionBinding: null);
        var descriptors = new List<AgentToolDescriptor>();
        descriptors.AddRange(_extensionCatalog.GetExtensions(PackageExtensionPoints.Tools).Select(tool => tool.Descriptor));
        foreach (var source in _extensionCatalog.GetExtensions(PackageExtensionPoints.ToolSources))
        {
            descriptors.AddRange(await source.ListToolsAsync(context));
        }

        return descriptors
            .Where(descriptor => descriptor.SelectionScope == AgentToolSelectionScope.Tool)
            .GroupBy(descriptor => string.Concat(descriptor.SourceId ?? string.Empty, "\n", descriptor.ToolId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(descriptor => descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListSelectableProfileCapabilitiesAsync()
    {
        if (_extensionCatalog is null)
        {
            return [];
        }

        _capabilityChangeObserver?.RefreshProviderSubscriptions();
        var capabilities = new List<AgentProfileSelectableCapabilityDescriptor>();
        var request = new AgentProfileSelectableCapabilityRequest(Profile: null);
        foreach (var provider in _extensionCatalog.GetExtensions(PackageExtensionPoints.ProfileSelectableCapabilityProviders))
        {
            capabilities.AddRange(await provider.ListCapabilitiesAsync(request));
        }

        return capabilities
            .Where(capability => !string.IsNullOrWhiteSpace(capability.Kind)
                                 && !string.IsNullOrWhiteSpace(capability.CapabilityId)
                                 && !string.IsNullOrWhiteSpace(capability.DisplayName))
            .GroupBy(capability => string.Concat(capability.Kind, "\n", capability.SourceId ?? string.Empty, "\n", capability.CapabilityId), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(capability => capability.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> GetEffectiveCapabilityAssignments(SubagentRecord subagent)
    {
        var assignments = new List<AgentProfileSelectableCapabilityAssignmentRecord>(subagent.SelectableCapabilityAssignments ?? []);
        if (string.Equals(_loadedCapabilitySubagentId, subagent.SubagentId, StringComparison.OrdinalIgnoreCase))
        {
            assignments.AddRange(CapabilityOptions
                .Where(option => option.IsEnabled)
                .Select(option => new AgentProfileSelectableCapabilityAssignmentRecord(option.Kind, option.CapabilityId, option.SourceId)));
        }

        return assignments.Distinct().ToArray();
    }

    private static bool IsEnabled(
        IReadOnlyList<AgentProfileSelectableCapabilityAssignmentRecord> assignments,
        string kind,
        string capabilityId,
        string? sourceId)
        => assignments.Any(assignment => string.Equals(assignment.Kind, kind, StringComparison.OrdinalIgnoreCase)
                                         && string.Equals(assignment.CapabilityId, capabilityId, StringComparison.OrdinalIgnoreCase)
                                         && (string.IsNullOrWhiteSpace(sourceId)
                                              || string.Equals(assignment.SourceId, sourceId, StringComparison.OrdinalIgnoreCase)));

    private static SubagentCapabilityGroupInfo ResolveToolGroup(AgentToolDescriptor descriptor)
    {
        var title = FirstNonEmpty(
            descriptor.SelectionGroupDisplayName,
            descriptor.SourceDisplayName,
            HumanizeIdentifier(descriptor.SelectionGroupId),
            HumanizeIdentifier(descriptor.SourceId),
            "Tools")!;
        var key = FirstNonEmpty(
            descriptor.SelectionGroupId,
            descriptor.SourceId,
            descriptor.SourceKind,
            title)!;
        return new SubagentCapabilityGroupInfo("tool:" + key, title, descriptor.SelectionGroupDescription, 10);
    }

    private static SubagentCapabilityGroupInfo ResolvePackageCapabilityGroup(AgentProfileSelectableCapabilityDescriptor capability)
    {
        var kind = capability.Kind;
        var sourceId = capability.SourceId;
        if (string.Equals(kind, AgentProfileSelectableCapabilityKinds.Subagent, StringComparison.OrdinalIgnoreCase))
        {
            return new SubagentCapabilityGroupInfo("subagents", "Subagents", "Delegated specialists available to orchestrated profiles.", 40);
        }

        if (!string.IsNullOrWhiteSpace(capability.GroupDisplayName))
        {
            var key = FirstNonEmpty(capability.GroupId, capability.SourceId, capability.Kind, capability.GroupDisplayName)!;
            return new SubagentCapabilityGroupInfo(
                "package:" + key,
                capability.GroupDisplayName.Trim(),
                capability.GroupDescription,
                capability.GroupSortOrder);
        }

        if (string.Equals(kind, "skill", StringComparison.OrdinalIgnoreCase))
        {
            return new SubagentCapabilityGroupInfo("skills", "Skills", "Reusable skill packages exposed to the agent.", 30);
        }

        if (string.Equals(kind, AgentProfileSelectableCapabilityKinds.ToolGroup, StringComparison.OrdinalIgnoreCase)
            && string.Equals(sourceId, "mcp", StringComparison.OrdinalIgnoreCase))
        {
            return new SubagentCapabilityGroupInfo("mcp", "MCP Servers", "Configured Model Context Protocol servers.", 20);
        }

        if (string.Equals(kind, AgentProfileSelectableCapabilityKinds.ToolGroup, StringComparison.OrdinalIgnoreCase))
        {
            return new SubagentCapabilityGroupInfo("tool-groups:" + (sourceId ?? string.Empty), "Tool Groups", "Package-provided groups of related tools.", 25);
        }

        if (string.Equals(kind, AgentProfileSelectableCapabilityKinds.Tool, StringComparison.OrdinalIgnoreCase))
        {
            return new SubagentCapabilityGroupInfo("tools:" + (sourceId ?? string.Empty), "Tools", "Package-provided individual tools.", 10);
        }

        return new SubagentCapabilityGroupInfo("other:" + kind, "Other", "Additional package capabilities.", 90);
    }

    private static string? FirstNonEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static string? HumanizeIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return string.Join(" ", value.Split(['-', '_', '.'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(part => string.IsNullOrEmpty(part) ? part : char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private void OnSelectableCapabilitiesChanged()
        => QueueCapabilitiesRefresh();

    private void QueueCapabilitiesRefresh()
        => RunOnUiThread(() => _ = RefreshSelectedSubagentCapabilitiesSafelyAsync());

    private async Task RefreshSelectedSubagentCapabilitiesSafelyAsync()
    {
        try
        {
            await RefreshSelectedSubagentCapabilitiesAsync();
        }
        catch (Exception ex)
        {
            if (SelectedSubagent is not null)
            {
                SetStatus(ex.Message, SubagentStatusKind.Error);
            }
        }
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
        if (_capabilityChangeObserver is not null)
        {
            _capabilityChangeObserver.Changed -= OnSelectableCapabilitiesChanged;
            _capabilityChangeObserver.Dispose();
        }
    }

    private void ClearStatus()
        => SetStatus(string.Empty, SubagentStatusKind.None);

    private void SetStatus(string message, SubagentStatusKind kind, bool autoClear = false)
    {
        CancelSuccessStatusClear();
        StatusKind = string.IsNullOrWhiteSpace(message) ? SubagentStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == SubagentStatusKind.Success)
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

    private async Task ClearSuccessStatusAfterDelayAsync(string message, CancellationTokenSource cancellation)
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
            if (_successStatusClearCancellation == cancellation
                && StatusKind == SubagentStatusKind.Success
                && string.Equals(StatusText, message, StringComparison.Ordinal))
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

    private void ApplyChatProviderReadiness(AgentProviderReadiness? readiness)
    {
        if (!HasChatProviderChoices || !HasSelectedChatProvider || readiness is null || readiness.Status == AgentProviderReadinessStatus.Ready)
        {
            ClearChatProviderWarning();
            return;
        }

        SetChatProviderWarning(readiness.Message);
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

    private void NotifyChatProviderStateChanged()
    {
        OnPropertyChanged(nameof(HasNoChatProviderChoices));
        OnPropertyChanged(nameof(ShowChatProviderPicker));
        OnPropertyChanged(nameof(ShowChatProviderWarning));
        OnPropertyChanged(nameof(ShowChatModelSelection));
        OnPropertyChanged(nameof(ShowReasoningOptions));
        OnPropertyChanged(nameof(CanOpenChatProviderSettings));
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

}

public enum SubagentStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

public sealed record SubagentCapabilityGroupViewModel(
    string Title,
    string? Description,
    int SortOrder,
    IReadOnlyList<SubagentCapabilityOptionViewModel> Options)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

internal sealed record SubagentCapabilityGroupInfo(string Key, string Title, string? Description, int SortOrder);

public sealed partial class SubagentCapabilityOptionViewModel(
    string kind,
    string capabilityId,
    string? sourceId,
    string displayName,
    string? description,
    bool isEnabled,
    string? groupKey = null,
    string? groupTitle = null,
    string? groupDescription = null,
    int groupSortOrder = 0) : ObservableObject
{
    public string Kind { get; } = kind;

    public string CapabilityId { get; } = capabilityId;

    public string? SourceId { get; } = sourceId;

    public string DisplayName { get; } = displayName;

    public string? Description { get; } = description;

    public string GroupKey { get; } = string.IsNullOrWhiteSpace(groupKey) ? kind : groupKey.Trim();

    public string GroupTitle { get; } = string.IsNullOrWhiteSpace(groupTitle) ? kind : groupTitle.Trim();

    public string? GroupDescription { get; } = string.IsNullOrWhiteSpace(groupDescription) ? null : groupDescription.Trim();

    public int GroupSortOrder { get; } = groupSortOrder;

    [ObservableProperty]
    private bool _isEnabled = isEnabled;
}

public sealed record SubagentProviderOption(string? ProviderId, string Label, string? PackageId = null);

public sealed record SubagentModelOption(string ModelId, string Label, IReadOnlyList<AgentModelVariantDescriptor>? Variants = null);

public sealed record SubagentReasoningOption(string? VariantId, string Label, string? Description = null)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}
