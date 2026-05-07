using System.Collections.ObjectModel;
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
    private readonly AgentWorkspaceService _workspaceService;
    private readonly AgentExecutionTargetService _targetService;
    private readonly IPackageExtensionCatalog _extensionCatalog;
    private readonly AgentExecutionTargetWarmupService? _warmupService;
    private bool _suppressSelectionHandlers;

    public AgentWorkspacesViewModel(
        AgentWorkspaceService workspaceService,
        AgentExecutionTargetService targetService,
        IPackageExtensionCatalog extensionCatalog,
        AgentExecutionTargetWarmupService? warmupService = null)
    {
        _workspaceService = workspaceService;
        _targetService = targetService;
        _extensionCatalog = extensionCatalog;
        _warmupService = warmupService;
        ReloadTargets();
        ReloadWorkspaces(selectWorkspaceId: null);
    }

    public ObservableCollection<AgentWorkspaceRecord> Workspaces { get; } = [];

    public ObservableCollection<ExecutionTargetOption> ExecutionTargets { get; } = [];

    public bool HasExecutionTargetChoices => ExecutionTargets.Any(target => !target.IsUnconfigured);

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
    private string _statusText = string.Empty;

    public bool CanDeleteSelected => SelectedWorkspace is not null;

    partial void OnSelectedWorkspaceChanged(AgentWorkspaceRecord? value)
    {
        DeleteWorkspaceCommand.NotifyCanExecuteChanged();
        SaveWorkspaceCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanDeleteSelected));
        OnPropertyChanged(nameof(HasSelectedWorkspace));

        if (_suppressSelectionHandlers)
        {
            return;
        }

        LoadWorkspace(value);
    }

    public void Dispose()
    {
    }

    partial void OnIsCompactLayoutChanged(bool value)
    {
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
            StatusText = "Workspace created.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
            var editorSaveResult = await SaveEditorSectionsAsync();
            if (!editorSaveResult.Success)
            {
                StatusText = editorSaveResult.Message;
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
                    StatusText = "Workspace saved. Preparing execution target...";
                    warmupResult = await _warmupService.WarmWorkspaceAsync(SelectedWorkspace);
                }
            }

            ReloadWorkspaces(SelectedWorkspace.WorkspaceId);
            StatusText = warmupResult?.Status == AgentExecutionTargetWarmupStatus.Failed
                ? $"Workspace saved, but execution target is not ready: {warmupResult.Message}"
                : warmupResult?.Status == AgentExecutionTargetWarmupStatus.Ready
                    ? "Workspace saved. Execution target is ready."
                    : "Workspace saved.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
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
            _workspaceService.DeleteWorkspace(SelectedWorkspace.WorkspaceId);
            ReloadWorkspaces(selectWorkspaceId: null);
            IsEditorActive = false;
            StatusText = "Workspace deleted.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanSaveWorkspace() => SelectedWorkspace is not null;

    private bool CanDeleteWorkspace() => SelectedWorkspace is not null;

    [RelayCommand]
    private void BackToWorkspaceList()
        => IsEditorActive = false;

    [RelayCommand]
    private void OpenWorkspaceEditor(AgentWorkspaceRecord workspace)
    {
        SelectedWorkspace = workspace;
        LoadWorkspace(workspace);
        IsEditorActive = true;
    }

    private void ReloadTargets()
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

        OnPropertyChanged(nameof(HasExecutionTargetChoices));
    }

    private void ReloadWorkspaces(string? selectWorkspaceId)
    {
        var workspaces = _workspaceService.ListWorkspaces();
        Workspaces.Clear();
        foreach (var workspace in workspaces)
        {
            Workspaces.Add(workspace);
        }

        _suppressSelectionHandlers = true;
        try
        {
            SelectedWorkspace = Workspaces.FirstOrDefault(workspace => string.Equals(workspace.WorkspaceId, selectWorkspaceId, StringComparison.OrdinalIgnoreCase))
                ?? Workspaces.FirstOrDefault();
        }
        finally
        {
            _suppressSelectionHandlers = false;
        }

        LoadWorkspace(SelectedWorkspace);
    }

    private void LoadWorkspace(AgentWorkspaceRecord? workspace)
    {
        DisplayName = workspace?.DisplayName ?? string.Empty;
        Description = workspace?.Description ?? string.Empty;
        var binding = workspace is null
            ? null
            : _workspaceService.ListBindings(workspace.WorkspaceId)
                .FirstOrDefault(item => string.Equals(item.Role, AgentWorkspaceBindingRoles.PrimaryExecutionTarget, StringComparison.OrdinalIgnoreCase));
        SelectedExecutionTarget = ResolveTargetOption(binding?.ContributionId);
        _ = RefreshEditorSectionsAsync();
    }

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

            StatusText = string.Empty;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
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
}

public sealed class AgentEditorSectionViewModel : ObservableObject
{
    private readonly IAgentWorkspaceEditorContributor _contributor;
    private readonly AgentWorkspaceEditorContext _context;

    public AgentEditorSectionViewModel(
        IAgentWorkspaceEditorContributor contributor,
        AgentWorkspaceEditorContext context,
        AgentEditorSection section)
    {
        _contributor = contributor;
        _context = context;
        SectionId = section.SectionId;
        Title = section.Title;
        Description = section.Description ?? string.Empty;
        Fields = new ObservableCollection<AgentEditorFieldViewModel>(section.Fields.Select(CreateField));
    }

    public string SectionId { get; }

    public string Title { get; }

    public string Description { get; }

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public ObservableCollection<AgentEditorFieldViewModel> Fields { get; }

    public ValueTask<AgentEditorSaveResult> SaveAsync()
        => _contributor.SaveSectionAsync(
            _context,
            new AgentEditorSaveRequest(
                SectionId,
                Fields.ToDictionary(field => field.FieldId, field => field.ToValue(), StringComparer.OrdinalIgnoreCase)));

    private static AgentEditorFieldViewModel CreateField(AgentEditorField field)
        => field.Kind switch
        {
            AgentEditorFieldKind.Select => new AgentEditorSelectFieldViewModel(field),
            AgentEditorFieldKind.PathList => new AgentEditorPathListFieldViewModel(field),
            _ => new AgentEditorTextFieldViewModel(field),
        };
}

public abstract partial class AgentEditorFieldViewModel(AgentEditorField field) : ObservableObject
{
    public string FieldId { get; } = field.FieldId;

    public string Label { get; } = field.Label;

    public string Description { get; } = field.Description ?? string.Empty;

    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

    public abstract AgentEditorFieldValue ToValue();
}

public sealed partial class AgentEditorTextFieldViewModel(AgentEditorField field) : AgentEditorFieldViewModel(field)
{
    [ObservableProperty]
    private string _value = field.Value ?? string.Empty;

    public override AgentEditorFieldValue ToValue() => new(Value);
}

public sealed partial class AgentEditorSelectFieldViewModel : AgentEditorFieldViewModel
{
    public AgentEditorSelectFieldViewModel(AgentEditorField field)
        : base(field)
    {
        Options = new ObservableCollection<AgentEditorOptionViewModel>((field.Options ?? [])
            .Select(option => new AgentEditorOptionViewModel(option.Value, option.Label, option.Description ?? string.Empty)));
        SelectedOption = Options.FirstOrDefault(option => string.Equals(option.Value, field.Value, StringComparison.OrdinalIgnoreCase))
                         ?? Options.FirstOrDefault();
    }

    public ObservableCollection<AgentEditorOptionViewModel> Options { get; }

    [ObservableProperty]
    private AgentEditorOptionViewModel? _selectedOption;

    public override AgentEditorFieldValue ToValue() => new(SelectedOption?.Value);
}

public sealed partial class AgentEditorPathListFieldViewModel : AgentEditorFieldViewModel
{
    public AgentEditorPathListFieldViewModel(AgentEditorField field)
        : base(field)
    {
        AddItemLabel = string.IsNullOrWhiteSpace(field.AddItemLabel) ? "Add" : field.AddItemLabel;
        UseFolderPicker = field.UseFolderPicker;
        DefaultNewItemValue = string.IsNullOrWhiteSpace(field.DefaultNewItemValue) ? string.Empty : field.DefaultNewItemValue;
        Items = new ObservableCollection<AgentEditorPathListItemViewModel>((field.Items ?? [])
            .Select(item => new AgentEditorPathListItemViewModel(item.Value, item.IsDefault)));
        SelectedItem = Items.FirstOrDefault(item => item.IsDefault) ?? Items.FirstOrDefault();
    }

    public string AddItemLabel { get; }

    public bool UseFolderPicker { get; }

    public string DefaultNewItemValue { get; }

    public ObservableCollection<AgentEditorPathListItemViewModel> Items { get; }

    [ObservableProperty]
    private AgentEditorPathListItemViewModel? _selectedItem;

    public void AddItem(string value)
    {
        var item = new AgentEditorPathListItemViewModel(value, Items.Count == 0);
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
            .Select((item, index) => new AgentEditorListItem(index.ToString(), item.Value, item.IsDefault))
            .ToArray());
}

public sealed record AgentEditorOptionViewModel(string Value, string Label, string Description)
{
    public bool HasDescription => !string.IsNullOrWhiteSpace(Description);
}

public sealed partial class AgentEditorPathListItemViewModel(string value, bool isDefault) : ObservableObject
{
    [ObservableProperty]
    private string _value = value;

    [ObservableProperty]
    private bool _isDefault = isDefault;
}

public sealed record ExecutionTargetOption(string? TargetId, string DisplayName, string Description)
{
    public bool IsUnconfigured => string.IsNullOrWhiteSpace(TargetId);

    public static ExecutionTargetOption Unconfigured { get; } = new(null, "Unconfigured", "Chat-only workspace. Execution-backed tools will be unavailable.");
}
