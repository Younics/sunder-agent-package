using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.PackageViews;

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

public sealed partial class AgentEditorTextFieldViewModel(AgentEditorSectionViewModel section, AgentEditorField field)
    : AgentEditorFieldViewModel(section, field)
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

public sealed partial class AgentWorkspacePathItemViewModel : ObservableObject
{
    public AgentWorkspacePathItemViewModel(AgentWorkspacePathRecord path)
    {
        PathId = path.PathId;
        HostPath = AgentWorkspacePathFormatter.FormatForDisplay(path.HostPath);
        IsDefault = path.IsDefault;
        CreatedAtUtc = path.CreatedAtUtc;
    }

    public string PathId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    [ObservableProperty]
    private string _hostPath = string.Empty;

    [ObservableProperty]
    private bool _isDefault;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditActive))]
    private bool _isEditActive;

    [ObservableProperty]
    private string _editHostPath = string.Empty;

    public bool IsNotEditActive => !IsEditActive;

    public void BeginEdit()
    {
        EditHostPath = HostPath;
        IsEditActive = true;
    }

    public void SaveEdit()
    {
        HostPath = AgentWorkspacePathFormatter.FormatForDisplayOrRaw(EditHostPath);
        CancelEdit();
    }

    public void CancelEdit()
    {
        IsEditActive = false;
        EditHostPath = string.Empty;
    }

    public AgentWorkspacePathRecord ToRecord(string workspaceId, int sortOrder)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspacePathRecord(
            PathId,
            workspaceId,
            HostPath,
            IsDefault,
            sortOrder,
            CreatedAtUtc == default ? now : CreatedAtUtc,
            now);
    }
}

internal static class AgentWorkspacePathFormatter
{
    public static string FormatForDisplay(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var fullPath = Path.GetFullPath(ExpandPath(path.Trim()));
        if (!string.IsNullOrWhiteSpace(home)
            && IsSameOrChildPath(fullPath, home))
        {
            var relative = fullPath[Path.GetFullPath(home).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Length..]
                .TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return string.IsNullOrWhiteSpace(relative)
                ? "~"
                : $"~/{relative.Replace(Path.DirectorySeparatorChar, '/')}";
        }

        return fullPath.Replace(Path.DirectorySeparatorChar, '/');
    }

    public static string FormatForDisplayOrRaw(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return FormatForDisplay(path);
        }
        catch
        {
            return path.Trim();
        }
    }

    public static string GetFullPath(string path)
        => Path.GetFullPath(ExpandPath(path.Trim()));

    private static string ExpandPath(string path)
    {
        if (path == "~")
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        }

        return path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..])
            : Environment.ExpandEnvironmentVariables(path);
    }

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        var candidate = Path.GetFullPath(candidatePath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = Path.GetFullPath(rootPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(candidate, root, comparison)
               || candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison)
               || candidate.StartsWith(root + Path.AltDirectorySeparatorChar, comparison);
    }
}

public sealed partial class AgentWorkspaceDocumentItemViewModel : ObservableObject
{
    public AgentWorkspaceDocumentItemViewModel(AgentWorkspaceDocumentRecord document)
    {
        DocumentId = document.DocumentId;
        FilePath = AgentWorkspacePathFormatter.FormatForDisplay(document.FilePath);
        CreatedAtUtc = document.CreatedAtUtc;
    }

    public string DocumentId { get; }

    public DateTimeOffset CreatedAtUtc { get; }

    [ObservableProperty]
    private string _filePath = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotEditActive))]
    private bool _isEditActive;

    [ObservableProperty]
    private string _editFilePath = string.Empty;

    public bool IsNotEditActive => !IsEditActive;

    public void BeginEdit()
    {
        EditFilePath = FilePath;
        IsEditActive = true;
    }

    public void SaveEdit()
    {
        FilePath = AgentWorkspacePathFormatter.FormatForDisplayOrRaw(EditFilePath);
        CancelEdit();
    }

    public void CancelEdit()
    {
        IsEditActive = false;
        EditFilePath = string.Empty;
    }

    public AgentWorkspaceDocumentRecord ToRecord(string workspaceId, int sortOrder)
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentWorkspaceDocumentRecord(
            DocumentId,
            workspaceId,
            FilePath,
            sortOrder,
            CreatedAtUtc == default ? now : CreatedAtUtc,
            now);
    }
}
