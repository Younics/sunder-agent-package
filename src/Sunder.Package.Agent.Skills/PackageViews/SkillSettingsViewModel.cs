using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Skills.Services;

namespace Sunder.Package.Agent.Skills.PackageViews;

public sealed partial class SkillSettingsViewModel : ObservableObject
{
    private readonly SkillStore _store;
    private readonly SkillImportService _importService;

    public SkillSettingsViewModel(SkillStore store, SkillImportService importService)
    {
        _store = store;
        _importService = importService;
        Reload();
    }

    public ObservableCollection<InstalledSkillItemViewModel> Skills { get; } = [];

    public bool HasSelectedSkill => SelectedSkill is not null;

    public bool IsListActive => !IsDetailActive;

    public bool ShowWideLayout => !IsCompactLayout;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactDetail => IsCompactLayout && IsDetailActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowDetailPane => ShowWideLayout || ShowCompactDetail;

    [ObservableProperty]
    private InstalledSkillItemViewModel? _selectedSkill;

    [ObservableProperty]
    private bool _isCompactLayout;

    [ObservableProperty]
    private bool _isDetailActive;

    [ObservableProperty]
    private string _githubUrl = string.Empty;

    [ObservableProperty]
    private string _statusText = string.Empty;

    [ObservableProperty]
    private bool _isBusy;

    partial void OnSelectedSkillChanged(InstalledSkillItemViewModel? value)
    {
        DeleteSelectedSkillCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedSkill));
    }

    partial void OnIsBusyChanged(bool value)
        => DeleteSelectedSkillCommand.NotifyCanExecuteChanged();

    partial void OnIsCompactLayoutChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactDetail));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowDetailPane));
    }

    partial void OnIsDetailActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactDetail));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowDetailPane));
    }

    [RelayCommand]
    private async Task ImportGitHubAsync()
    {
        if (string.IsNullOrWhiteSpace(GithubUrl))
        {
            StatusText = "Enter a GitHub skill folder URL.";
            return;
        }

        await RunImportAsync(() => _importService.ImportGitHubFolderAsync(GithubUrl.Trim()), "Imported skill from GitHub.");
    }

    public Task ImportLocalFolderAsync(string folderPath)
        => RunImportAsync(() => _importService.ImportLocalFolderAsync(folderPath), "Imported local skill folder.");

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedSkill))]
    private void DeleteSelectedSkill()
    {
        if (SelectedSkill is null)
        {
            return;
        }

        try
        {
            var displayName = SelectedSkill.DisplayName;
            _store.DeleteSkill(SelectedSkill.SkillId);
            Reload();
            if (SelectedSkill is null)
            {
                IsDetailActive = false;
            }

            StatusText = $"Deleted skill '{displayName}'.";
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
    }

    private bool CanDeleteSelectedSkill() => SelectedSkill is not null && !IsBusy;

    [RelayCommand]
    private void BackToSkillList()
        => IsDetailActive = false;

    [RelayCommand]
    private void NewSkill()
    {
        SelectedSkill = null;
        IsDetailActive = true;
        StatusText = "Import a skill from GitHub or select a local skill folder.";
    }

    [RelayCommand]
    private void OpenSkillDetail(InstalledSkillItemViewModel? skill)
    {
        if (skill is not null)
        {
            SelectedSkill = skill;
        }

        IsDetailActive = true;
    }

    private async Task RunImportAsync(Func<Task<InstalledSkillRecord>> action, string successMessage)
    {
        IsBusy = true;
        try
        {
            var imported = await action();
            Reload(imported.SkillId);
            IsDetailActive = true;
            StatusText = successMessage;
        }
        catch (Exception ex)
        {
            StatusText = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Reload(string? selectSkillId = null)
    {
        Skills.Clear();
        foreach (var skill in _store.ListSkills())
        {
            Skills.Add(new InstalledSkillItemViewModel(skill, _store.GetSkillRootPath(skill)));
        }

        SelectedSkill = Skills.FirstOrDefault(skill => string.Equals(skill.SkillId, selectSkillId, StringComparison.OrdinalIgnoreCase))
                        ?? Skills.FirstOrDefault();
    }
}

public sealed class InstalledSkillItemViewModel
{
    public InstalledSkillItemViewModel(InstalledSkillRecord skill, string rootPath)
    {
        SkillId = skill.SkillId;
        DisplayName = SkillStore.ResolveDisplayName(skill);
        Description = skill.Description ?? string.Empty;
        Version = skill.Version ?? string.Empty;
        Author = skill.Author ?? string.Empty;
        Source = skill.SourceKind == "github" && !string.IsNullOrWhiteSpace(skill.SourceUri)
            ? skill.SourceUri
            : skill.SourceKind;
        ResolvedCommitSha = skill.ResolvedCommitSha ?? string.Empty;
        RootPath = rootPath;
        MetadataText = skill.Metadata.Count == 0
            ? "No additional metadata."
            : string.Join(Environment.NewLine, skill.Metadata.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).Select(pair => $"{pair.Key}: {pair.Value}"));
        WarningsText = skill.Warnings.Count == 0
            ? "No warnings."
            : string.Join(Environment.NewLine, skill.Warnings);
    }

    public string SkillId { get; }

    public string DisplayName { get; }

    public string Description { get; }

    public string Version { get; }

    public string Author { get; }

    public string Source { get; }

    public string ResolvedCommitSha { get; }

    public string RootPath { get; }

    public string MetadataText { get; }

    public string WarningsText { get; }
}
