using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.PackageViews;

public sealed partial class AgentWorkspacesViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly AgentWorkspaceService _workspaceService;
    private readonly AgentExecutionTargetService _targetService;
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly IPackageExtensionCatalogMonitor? _extensionCatalogMonitor;
    private readonly IPackageExtensionCatalogChangeNotifier? _extensionCatalogChangeNotifier;
    private readonly AgentExecutionTargetWarmupService? _warmupService;
    private readonly IPackageSettingsNavigationService? _settingsNavigationService;
    private CancellationTokenSource? _successStatusClearCancellation;
    private bool _suppressSelectionHandlers;
    private bool _disposed;

    public AgentWorkspacesViewModel(
        AgentWorkspaceService workspaceService,
        AgentExecutionTargetService targetService,
        IPackageExtensionCatalog extensionCatalog,
        AgentExecutionTargetWarmupService? warmupService = null,
        IPackageSettingsNavigationService? settingsNavigationService = null)
    {
        _workspaceService = workspaceService;
        _targetService = targetService;
        _extensionCatalog = extensionCatalog;
        _settingsNavigationService = settingsNavigationService;
        _extensionCatalogMonitor = extensionCatalog as IPackageExtensionCatalogMonitor;
        if (_extensionCatalogMonitor is not null)
        {
            _extensionCatalogMonitor.Changed += OnExtensionCatalogChanged;
        }
        else if (extensionCatalog is IPackageExtensionCatalogChangeNotifier changeNotifier)
        {
            _extensionCatalogChangeNotifier = changeNotifier;
            changeNotifier.ExtensionsChanged += OnExtensionCatalogChanged;
        }

        _warmupService = warmupService;
        ReloadTargets();
        ReloadWorkspaces(selectWorkspaceId: null);
    }

    public ObservableCollection<AgentWorkspaceRecord> Workspaces { get; } = [];

    public ObservableCollection<ExecutionTargetOption> ExecutionTargets { get; } = [];

    public bool HasExecutionTargetChoices => ExecutionTargets.Any(target => !target.IsUnconfigured);

    public bool HasNoExecutionTargetChoices => !HasExecutionTargetChoices;

    public bool HasSelectedWorkspace => SelectedWorkspace is not null;

    public bool IsListActive => !IsEditorActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactEditor => IsCompactLayout && IsEditorActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowEditorPane => ShowWideLayout || ShowCompactEditor;

    [ObservableProperty]
    private AgentWorkspaceRecord? _selectedWorkspace;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isEditorActive;

    [ObservableProperty]
    private string _displayName = string.Empty;

    [ObservableProperty]
    private string _description = string.Empty;

    [ObservableProperty]
    private ExecutionTargetOption? _selectedExecutionTarget;

    public ObservableCollection<AgentEditorSectionViewModel> EditorSections { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasStatusText))]
    private string _statusText = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private AgentWorkspaceStatusKind _statusKind = AgentWorkspaceStatusKind.None;

    public bool HasStatusText => !string.IsNullOrWhiteSpace(StatusText);

    public bool IsStatusSuccess => StatusKind == AgentWorkspaceStatusKind.Success;

    public bool IsStatusWarning => StatusKind == AgentWorkspaceStatusKind.Warning;

    public bool IsStatusError => StatusKind == AgentWorkspaceStatusKind.Error;

    partial void OnSelectedWorkspaceChanged(AgentWorkspaceRecord? value)
    {
        DeleteWorkspaceCommand.NotifyCanExecuteChanged();
        SaveWorkspaceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedWorkspace));

        if (_suppressSelectionHandlers)
        {
            return;
        }

        LoadWorkspace(value);
        if (IsCompactLayout && value is not null)
        {
            IsEditorActive = true;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        CancelSuccessStatusClear();
        if (_extensionCatalogMonitor is not null)
        {
            _extensionCatalogMonitor.Changed -= OnExtensionCatalogChanged;
        }

        if (_extensionCatalogChangeNotifier is not null)
        {
            _extensionCatalogChangeNotifier.ExtensionsChanged -= OnExtensionCatalogChanged;
        }
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsEditorActive)
        {
            SelectedWorkspace = null;
        }
        else if (!value && SelectedWorkspace is null)
        {
            SelectedWorkspace = Workspaces.FirstOrDefault();
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

    partial void OnSelectedExecutionTargetChanged(ExecutionTargetOption? value)
    {
        if (_suppressSelectionHandlers)
        {
            return;
        }

        _ = RefreshEditorSectionsAsync();
    }

    [RelayCommand]
    private void CreateWorkspace()
    {
        try
        {
            var workspace = _workspaceService.CreateWorkspace("New Workspace");
            ReloadWorkspaces(workspace.WorkspaceId);
            IsEditorActive = true;
            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanSaveWorkspace))]
    private async Task SaveWorkspace()
    {
        if (SelectedWorkspace is null)
        {
            return;
        }

        try
        {
            var savedWorkspaceId = SelectedWorkspace.WorkspaceId;
            var editorSaveResult = await SaveEditorSectionsAsync();
            if (!editorSaveResult.Success)
            {
                SetStatus(editorSaveResult.Message, AgentWorkspaceStatusKind.Error);
                return;
            }

            _workspaceService.SaveWorkspace(SelectedWorkspace.WorkspaceId, DisplayName, Description);
            AgentExecutionTargetWarmupResult? warmupResult = null;
            if (SelectedExecutionTarget is null || SelectedExecutionTarget.IsUnconfigured)
            {
                _workspaceService.RemovePrimaryExecutionBinding(SelectedWorkspace.WorkspaceId);
            }
            else
            {
                _workspaceService.SavePrimaryExecutionBinding(SelectedWorkspace.WorkspaceId, SelectedExecutionTarget.TargetId!);
                if (_warmupService is not null)
                {
                    SetStatus("Workspace saved. Preparing execution target...", AgentWorkspaceStatusKind.Warning);
                    warmupResult = await _warmupService.WarmWorkspaceAsync(SelectedWorkspace);
                }
            }

            var shouldClearSelection = IsCompactLayout;
            ReloadWorkspaceList(savedWorkspaceId);
            if (shouldClearSelection)
            {
                SelectedWorkspace = null;
                ClearStatus();
            }
            else
            {
                var statusText = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? $"Workspace saved, but execution target is not ready: {warmupResult.Message}"
                    : warmupResult?.Status == AgentExecutionTargetWarmupStatus.Ready
                        ? "Workspace saved. Execution target is ready."
                        : "Workspace saved.";
                var statusKind = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                    ? AgentWorkspaceStatusKind.Warning
                    : AgentWorkspaceStatusKind.Success;

                SetStatus(statusText, statusKind, autoClear: statusKind == AgentWorkspaceStatusKind.Success);
            }

            IsEditorActive = false;
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteWorkspace))]
    private void DeleteWorkspace()
    {
        if (SelectedWorkspace is null)
        {
            return;
        }

        try
        {
            var shouldClearSelection = IsCompactLayout;
            _workspaceService.DeleteWorkspace(SelectedWorkspace.WorkspaceId);
            if (shouldClearSelection)
            {
                ReloadWorkspaceList(selectWorkspaceId: null);
                SelectedWorkspace = null;
            }
            else
            {
                ReloadWorkspaces(selectWorkspaceId: null);
            }

            IsEditorActive = false;
            if (shouldClearSelection)
            {
                ClearStatus();
            }
            else
            {
                SetStatus("Workspace deleted.", AgentWorkspaceStatusKind.Success, autoClear: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    private bool CanSaveWorkspace() => SelectedWorkspace is not null;

    private bool CanDeleteWorkspace() => SelectedWorkspace is not null;

    [RelayCommand]
    private void BackToWorkspaceList()
    {
        if (IsCompactLayout)
        {
            SelectedWorkspace = null;
        }

        IsEditorActive = false;
    }

    public async Task ExecuteEditorActionAsync(AgentEditorActionViewModel action)
    {
        try
        {
            switch (action.Kind)
            {
                case AgentEditorActionKind.OpenPackageSettings:
                    if (_settingsNavigationService is null || string.IsNullOrWhiteSpace(action.PackageId))
                    {
                        SetStatus("Package settings cannot be opened from this host.", AgentWorkspaceStatusKind.Warning);
                        return;
                    }

                    if (await _settingsNavigationService.OpenPackageSettingsAsync(action.PackageId, action.Parameters))
                    {
                        SetStatus("Opened package settings.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    }
                    else
                    {
                        SetStatus("Package settings could not be opened.", AgentWorkspaceStatusKind.Warning);
                    }

                    break;
                case AgentEditorActionKind.RefreshEditor:
                    await RefreshEditorSectionsAsync();
                    SetStatus("Workspace editor refreshed.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    break;
                case AgentEditorActionKind.RefreshField:
                    await RefreshEditorFieldAsync(action.Field);
                    SetStatus("Workspace editor field refreshed.", AgentWorkspaceStatusKind.Success, autoClear: true);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    [RelayCommand]
    private void OpenWorkspaceEditor(AgentWorkspaceRecord workspace)
    {
        ActivateWorkspace(workspace);
        IsEditorActive = true;
    }

    public void ActivateWorkspace(AgentWorkspaceRecord workspace)
    {
        if (!string.Equals(SelectedWorkspace?.WorkspaceId, workspace.WorkspaceId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedWorkspace = workspace;
        }

        if (IsCompactLayout)
        {
            IsEditorActive = true;
        }
    }

    private void ReloadTargets(string? preferredTargetId = null)
    {
        ExecutionTargets.Clear();
        ExecutionTargets.Add(ExecutionTargetOption.Unconfigured);
        foreach (var target in _targetService.ListTargets())
        {
            ExecutionTargets.Add(new ExecutionTargetOption(
                target.TargetId,
                target.DisplayName,
                target.Description ?? target.TargetId));
        }

        if (preferredTargetId is not null)
        {
            SetSelectionSilently(() => SelectedExecutionTarget = ResolveTargetOption(preferredTargetId));
        }

        OnPropertyChanged(nameof(HasExecutionTargetChoices));
        OnPropertyChanged(nameof(HasNoExecutionTargetChoices));
    }

    private void OnExtensionCatalogChanged(object? sender, EventArgs e)
        => RunOnUiThread(ApplyExtensionCatalogChanges);

    private void OnExtensionCatalogChanged(object? sender, PackageExtensionCatalogChangedEventArgs e)
    {
        if (!e.IncludesExtensionPoint(PackageExtensionPoints.ExecutionTargets.Id)
            && !e.IncludesExtensionPoint(PackageExtensionPoints.WorkspaceEditorContributors.Id))
        {
            return;
        }

        RunOnUiThread(ApplyExtensionCatalogChanges);
    }

    private void ApplyExtensionCatalogChanges()
    {
        if (_disposed)
        {
            return;
        }

        var preferredTargetId = SelectedExecutionTarget?.TargetId;
        if (string.IsNullOrWhiteSpace(preferredTargetId) && SelectedWorkspace is not null)
        {
            preferredTargetId = ResolveWorkspaceTargetId(SelectedWorkspace.WorkspaceId);
        }

        ReloadTargets(preferredTargetId);
        _ = RefreshEditorSectionsAsync();
    }

    private void ReloadWorkspaces(string? selectWorkspaceId)
    {
        ReloadWorkspaceList(selectWorkspaceId);
        LoadWorkspace(SelectedWorkspace);
    }

    private void ReloadWorkspaceList(string? selectWorkspaceId)
    {
        var workspaces = _workspaceService.ListWorkspaces();
        _suppressSelectionHandlers = true;
        try
        {
            Workspaces.Clear();
            foreach (var workspace in workspaces)
            {
                Workspaces.Add(workspace);
            }

            SelectedWorkspace = Workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, selectWorkspaceId, StringComparison.OrdinalIgnoreCase))
                ?? Workspaces.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }
    }

    private void LoadWorkspace(AgentWorkspaceRecord? workspace)
    {
        DisplayName = workspace?.DisplayName ?? string.Empty;
        Description = workspace?.Description ?? string.Empty;
        var binding = workspace is null
            ? null
            : _workspaceService.ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(item => string.Equals(item.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
        SetSelectionSilently(() => SelectedExecutionTarget = ResolveTargetOption(binding?.ContributionId));
        _ = RefreshEditorSectionsAsync();
    }

    private string? ResolveWorkspaceTargetId(string workspaceId)
        => _workspaceService.ListBindings(workspaceId)
            .FirstOrDefault(item => string.Equals(item.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase))
            ?.ContributionId;

    private async Task RefreshEditorSectionsAsync()
    {
        EditorSections.Clear();
        var context = BuildEditorContext();
        if (context is null)
        {
            return;
        }

        try
        {
            var contributors = _extensionCatalog.GetExtensions(PackageExtensionPoints.WorkspaceEditorContributors)
                .Where(contributor => contributor.CanEdit(context))
                .ToArray();
            foreach (var contributor in contributors)
            {
                var sections = await contributor.GetSectionsAsync(context);
                foreach (var section in sections)
                {
                    EditorSections.Add(new AgentEditorSectionViewModel(contributor, context, section));
                }
            }

            ClearStatus();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, AgentWorkspaceStatusKind.Error);
        }
    }

    private async Task RefreshEditorFieldAsync(AgentEditorFieldViewModel field)
    {
        var context = BuildEditorContext();
        if (context is null || !field.Section.Contributor.CanEdit(context))
        {
            return;
        }

        var sections = await field.Section.Contributor.GetSectionsAsync(context);
        var refreshedSection = sections.FirstOrDefault(section => string.Equals(section.SectionId, field.Section.SectionId, StringComparison.OrdinalIgnoreCase));
        var refreshedField = refreshedSection?.Fields.FirstOrDefault(candidate => string.Equals(candidate.FieldId, field.FieldId, StringComparison.OrdinalIgnoreCase));
        if (refreshedField is null)
        {
            await RefreshEditorSectionsAsync();
            return;
        }

        field.ApplyField(refreshedField);
    }

    private AgentWorkspaceEditorContext? BuildEditorContext()
    {
        if (SelectedWorkspace is null || SelectedExecutionTarget is null || SelectedExecutionTarget.IsUnconfigured)
        {
            return null;
        }

        return new AgentWorkspaceEditorContext(
            SelectedWorkspace,
            SelectedExecutionTarget.TargetId!,
            AgentWorkspaceService.BuildPrimaryBindingId(SelectedWorkspace.WorkspaceId));
    }

    private async Task<AgentEditorSaveResult> SaveEditorSectionsAsync()
    {
        foreach (var section in EditorSections)
        {
            var result = await section.SaveAsync();
            if (!result.Success)
            {
                return result;
            }
        }

        return AgentEditorSaveResult.Ok("Workspace editor sections saved.");
    }

    private ExecutionTargetOption ResolveTargetOption(string? contributionId)
        => ExecutionTargets.FirstOrDefault(target => string.Equals(target.TargetId, contributionId, StringComparison.OrdinalIgnoreCase))
           ?? ExecutionTargetOption.Unconfigured;

    private static void RunOnUiThread(Action action)
    {
        if (Avalonia.Application.Current is null || Dispatcher.UIThread.CheckAccess())
        {
            action();
            return;
        }

        Dispatcher.UIThread.Post(action, DispatcherPriority.Background);
    }

    private void ClearStatus()
        => SetStatus(string.Empty, AgentWorkspaceStatusKind.None);

    private void SetStatus(string message, AgentWorkspaceStatusKind kind, bool autoClear = false)
    {
        CancelSuccessStatusClear();
        StatusKind = string.IsNullOrWhiteSpace(message) ? AgentWorkspaceStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == AgentWorkspaceStatusKind.Success)
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
                && StatusKind == AgentWorkspaceStatusKind.Success
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

public enum AgentWorkspaceStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
}

public sealed class AgentEditorSectionViewModel : ObservableObject
{
    public AgentEditorSectionViewModel(
        IAgentWorkspaceEditorContributor contributor,
        AgentWorkspaceEditorContext context,
        AgentEditorSection section)
    {
        Contributor = contributor;
        Context = context;
        SectionId = section.SectionId;
        Title = section.Title;
        Description = section.Description ?? string.Empty;
        Fields = new ObservableCollection<AgentEditorFieldViewModel>(section.Fields.Select(CreateField));
    }

    internal IAgentWorkspaceEditorContributor Contributor { get; }

    internal AgentWorkspaceEditorContext Context { get; }

    public string SectionId { get; }

    public string Title { get; }

    public string Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ObservableCollection<AgentEditorFieldViewModel> Fields { get; }

    public ValueTask<AgentEditorSaveResult> SaveAsync()
        => Contributor.SaveSectionAsync(
            Context,
            new AgentEditorSaveRequest(
                SectionId,
                Fields.ToDictionary(field => field.FieldId, field => field.ToValue(), StringComparer.OrdinalIgnoreCase)));

    private AgentEditorFieldViewModel CreateField(AgentEditorField field)
        => field.Kind switch
        {
            AgentEditorFieldKind.Select => new AgentEditorSelectFieldViewModel(this, field),
            AgentEditorFieldKind.PathList => new AgentEditorPathListFieldViewModel(this, field),
            _ => new AgentEditorTextFieldViewModel(this, field),
        };
}

public abstract partial class AgentEditorFieldViewModel : ObservableObject
{
    protected AgentEditorFieldViewModel(AgentEditorSectionViewModel section, AgentEditorField field)
    {
        Section = section;
        FieldId = field.FieldId;
        Label = field.Label;
        Description = field.Description ?? string.Empty;
        ReplaceActions(field.Actions);
    }

    public AgentEditorSectionViewModel Section { get; }

    public string FieldId { get; }

    public string Label { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescription))]
    private string _description = string.Empty;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ObservableCollection<AgentEditorActionViewModel> Actions { get; } = [];

    public ObservableCollection<AgentEditorActionViewModel> IconActions { get; } = [];

    public ObservableCollection<AgentEditorActionViewModel> TextActions { get; } = [];

    public bool HasActions => Actions.Count > 0;

    public bool HasIconActions => IconActions.Count > 0;

    public bool HasTextActions => TextActions.Count > 0;

    internal virtual void ApplyField(AgentEditorField field)
    {
        Description = field.Description ?? string.Empty;
        ReplaceActions(field.Actions);
    }

    private void ReplaceActions(IReadOnlyList<AgentEditorAction>? actions)
    {
        Actions.Clear();
        IconActions.Clear();
        TextActions.Clear();
        foreach (var action in actions ?? [])
        {
            var viewModel = new AgentEditorActionViewModel(this, action);
            Actions.Add(viewModel);
            if (viewModel.IsIconAction)
            {
                IconActions.Add(viewModel);
            }
            else
            {
                TextActions.Add(viewModel);
            }
        }

        OnPropertyChanged(nameof(HasActions));
        OnPropertyChanged(nameof(HasIconActions));
        OnPropertyChanged(nameof(HasTextActions));
    }

    public abstract AgentEditorFieldValue ToValue();
}

public sealed class AgentEditorActionViewModel(AgentEditorFieldViewModel field, AgentEditorAction action)
{
    public AgentEditorFieldViewModel Field { get; } = field;

    public string ActionId { get; } = action.ActionId;

    public string Label { get; } = action.Label;

    public AgentEditorActionKind Kind { get; } = action.Kind;

    public bool IsIconAction => Kind == AgentEditorActionKind.RefreshField;

    public string Content => IsIconAction ? "↻" : Label;

    public string? PackageId { get; } = action.PackageId;

    public IReadOnlyDictionary<string, string?>? Parameters { get; } = action.Parameters;
}

public sealed partial class AgentEditorTextFieldViewModel(AgentEditorSectionViewModel section, AgentEditorField field) : AgentEditorFieldViewModel(section, field)
{
    [ObservableProperty]
    private string _value = field.Value ?? string.Empty;

    public override AgentEditorFieldValue ToValue() => new(Value);
}

public sealed partial class AgentEditorSelectFieldViewModel : AgentEditorFieldViewModel
{
    public AgentEditorSelectFieldViewModel(AgentEditorSectionViewModel section, AgentEditorField field)
        : base(section, field)
    {
        ReplaceOptions(field.Options, field.Value, previousValue: null);
    }

    public ObservableCollection<AgentEditorOptionViewModel> Options { get; } = [];

    public bool HasOptions => Options.Count > 0;

    public bool HasNoOptions => !HasOptions;

    public bool ShowEmptyStateActions => HasNoOptions && HasTextActions;

    [ObservableProperty]
    private AgentEditorOptionViewModel? _selectedOption;

    internal override void ApplyField(AgentEditorField field)
    {
        var previousValue = SelectedOption?.Value;
        base.ApplyField(field);
        ReplaceOptions(field.Options, field.Value, previousValue);
    }

    private void ReplaceOptions(IReadOnlyList<AgentEditorOption>? options, string? preferredValue, string? previousValue)
    {
        Options.Clear();
        foreach (var option in options ?? [])
        {
            Options.Add(new AgentEditorOptionViewModel(option.Value, option.Label, option.Description ?? string.Empty));
        }

        SelectedOption = Options.FirstOrDefault(option => string.Equals(option.Value, previousValue, StringComparison.OrdinalIgnoreCase))
                         ?? Options.FirstOrDefault(option => string.Equals(option.Value, preferredValue, StringComparison.OrdinalIgnoreCase))
                         ?? Options.FirstOrDefault();
        OnPropertyChanged(nameof(HasOptions));
        OnPropertyChanged(nameof(HasNoOptions));
        OnPropertyChanged(nameof(ShowEmptyStateActions));
    }

    public override AgentEditorFieldValue ToValue() => new(SelectedOption?.Value);
}

public sealed partial class AgentEditorPathListFieldViewModel : AgentEditorFieldViewModel
{
    public AgentEditorPathListFieldViewModel(AgentEditorSectionViewModel section, AgentEditorField field)
        : base(section, field)
    {
        AddItemLabel = string.IsNullOrWhiteSpace(field.AddItemLabel) ? "Add" : field.AddItemLabel;
        UseFolderPicker = field.UseFolderPicker;
        DefaultNewItemValue = string.IsNullOrWhiteSpace(field.DefaultNewItemValue) ? string.Empty : field.DefaultNewItemValue;
        ItemValueLabel = string.IsNullOrWhiteSpace(field.ItemValueLabel) ? string.Empty : field.ItemValueLabel;
        SecondaryItemValueLabel = string.IsNullOrWhiteSpace(field.SecondaryItemValueLabel) ? string.Empty : field.SecondaryItemValueLabel;
        UseSecondaryFolderPicker = field.UseSecondaryFolderPicker;
        DefaultNewSecondaryItemValue = string.IsNullOrWhiteSpace(field.DefaultNewSecondaryItemValue) ? string.Empty : field.DefaultNewSecondaryItemValue;
        Items = new ObservableCollection<AgentEditorPathListItemViewModel>((field.Items ?? [])
            .Select(item => CreateItem(item.Value, item.IsDefault, item.SecondaryValue)));
    }

    public string AddItemLabel { get; }

    public bool UseFolderPicker { get; }

    public string DefaultNewItemValue { get; }

    public string ItemValueLabel { get; }

    public string SecondaryItemValueLabel { get; }

    public bool HasSecondaryValue => !string.IsNullOrWhiteSpace(SecondaryItemValueLabel);

    public bool UseSecondaryFolderPicker { get; }

    public string DefaultNewSecondaryItemValue { get; }

    public ObservableCollection<AgentEditorPathListItemViewModel> Items { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedItem))]
    private AgentEditorPathListItemViewModel? _selectedItem;

    public bool HasSelectedItem => SelectedItem is not null;

    public void AddItem(string value)
    {
        var item = CreateItem(value, Items.Count == 0, DefaultNewSecondaryItemValue);
        Items.Add(item);
        SelectedItem = item;
    }

    public void AddDefaultItem()
    {
        var candidate = string.IsNullOrWhiteSpace(DefaultNewItemValue) ? string.Empty : DefaultNewItemValue;
        if (!string.IsNullOrWhiteSpace(candidate))
        {
            var suffix = 2;
            var baseCandidate = candidate;
            while (Items.Any(item => string.Equals(item.Value, candidate, StringComparison.Ordinal)))
            {
                candidate = baseCandidate + suffix++;
            }
        }

        AddItem(candidate);
    }

    public void DeleteSelectedItem()
    {
        if (SelectedItem is null)
        {
            return;
        }

        var wasDefault = SelectedItem.IsDefault;
        Items.Remove(SelectedItem);
        if (wasDefault && Items.Count > 0)
        {
            Items[0].IsDefault = true;
        }

        SelectedItem = Items.FirstOrDefault(item => item.IsDefault) ?? Items.FirstOrDefault();
    }

    public void SetSelectedItemAsDefault()
    {
        if (SelectedItem is null)
        {
            return;
        }

        foreach (var item in Items)
        {
            item.IsDefault = ReferenceEquals(item, SelectedItem);
        }
    }

    public override AgentEditorFieldValue ToValue()
        => new(Items: Items
            .Select((item, index) => new AgentEditorListItem(index.ToString(), item.Value, item.IsDefault)
            {
                SecondaryValue = item.SecondaryValue,
            })
            .ToArray());

    private AgentEditorPathListItemViewModel CreateItem(string value, bool isDefault, string? secondaryValue)
        => new(value, isDefault, secondaryValue ?? string.Empty, HasSecondaryValue, ItemValueLabel, SecondaryItemValueLabel, UseSecondaryFolderPicker);
}

public sealed record AgentEditorOptionViewModel(string Value, string Label, string Description)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

public sealed partial class AgentEditorPathListItemViewModel(
    string value,
    bool isDefault,
    string secondaryValue,
    bool hasSecondaryValue,
    string itemValueLabel,
    string secondaryItemValueLabel,
    bool useSecondaryFolderPicker) : ObservableObject
{
    public bool HasItemValueLabel => !string.IsNullOrWhiteSpace(ItemValueLabel);

    public string ItemValueLabel { get; } = itemValueLabel;

    public bool HasSecondaryValue { get; } = hasSecondaryValue;

    public string SecondaryItemValueLabel { get; } = secondaryItemValueLabel;

    public bool UseSecondaryFolderPicker { get; } = useSecondaryFolderPicker;

    [ObservableProperty]
    private string _value = value;

    [ObservableProperty]
    private string _secondaryValue = secondaryValue;

    [ObservableProperty]
    private bool _isDefault = isDefault;
}

public sealed record ExecutionTargetOption(string? TargetId, string DisplayName, string Description)
{
    public bool IsUnconfigured => string.IsNullOrWhiteSpace(TargetId);

    public static ExecutionTargetOption Unconfigured { get; } = new(null, "Unconfigured", "Chat-only workspace. Execution-backed tools will be unavailable.");
}
