using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;

namespace Sunder.Package.Agent.Skills.PackageViews;

public sealed partial class SkillSettingsViewModel : ObservableObject, IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);

    private readonly ISkillManagementGateway _gateway;
    private readonly TimedStatusController _successStatus = new();
    private readonly IPresentationDispatcher _uiDispatcher = PresentationDispatcher.Capture();
    private readonly PresentationTaskScope _tasks = new();
    private readonly Task _initialization;
    private bool _suppressSelectionHandlers;
    private bool _suppressSkillChangeNotifications;
    private bool _disposed;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } = [];

    public SkillSettingsViewModel(SkillStore store, SkillImportService importService)
        : this(new SkillLocalManagementGateway(store, importService))
    {
    }

    internal SkillSettingsViewModel(ISkillManagementGateway gateway)
    {
        _gateway = gateway;
        _gateway.SkillsChanged += OnSkillsChanged;
        _initialization = InitializeCoreAsync();
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
    [NotifyPropertyChangedFor(nameof(IsStatusSuccess))]
    [NotifyPropertyChangedFor(nameof(IsStatusWarning))]
    [NotifyPropertyChangedFor(nameof(IsStatusError))]
    private SkillStatusKind _statusKind = SkillStatusKind.None;

    [ObservableProperty]
    private bool _isBusy;

    public bool IsStatusSuccess => StatusKind == SkillStatusKind.Success;

    public bool IsStatusWarning => StatusKind == SkillStatusKind.Warning;

    public bool IsStatusError => StatusKind == SkillStatusKind.Error;

    public Task InitializeAsync() => _initialization;

    partial void OnSelectedSkillChanged(InstalledSkillItemViewModel? value)
    {
        DeleteSelectedSkillCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(HasSelectedSkill));
        if (_suppressSelectionHandlers)
        {
            return;
        }

        if (IsCompactLayout && value is not null)
        {
            IsDetailActive = true;
        }
    }

    partial void OnIsBusyChanged(bool value)
        => DeleteSelectedSkillCommand.NotifyCanExecuteChanged();

    partial void OnIsCompactLayoutChanged(bool value)
    {
        if (value && !IsDetailActive)
        {
            SelectedSkill = null;
        }
        else if (!value && SelectedSkill is null)
        {
            SelectedSkill = Skills.FirstOrDefault();
        }

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
            SetStatus("Enter a GitHub skill folder URL.", SkillStatusKind.Warning);
            return;
        }

        await RunImportAsync(token => _gateway.ImportGitHubAsync(GithubUrl.Trim(), token), "Imported skill from GitHub.");
    }

    public Task ImportLocalFolderAsync(string folderPath)
        => RunImportAsync(token => _gateway.ImportLocalAsync(folderPath, token), "Imported local skill folder.");

    [RelayCommand]
    private Task ImportCommonSkillFoldersAsync()
        => RunImportAsync(token => _gateway.ImportCommonAsync(token), "Imported common skill folder(s).");

    [RelayCommand(CanExecute = nameof(CanDeleteSelectedSkill))]
    private async Task DeleteSelectedSkillAsync()
    {
        if (SelectedSkill is null)
        {
            return;
        }

        try
        {
            var displayName = SelectedSkill.DisplayName;
            var shouldClearSelection = IsCompactLayout;
            _suppressSkillChangeNotifications = true;
            try
            {
                await _gateway.DeleteAsync(SelectedSkill.SkillId);
            }
            finally
            {
                _suppressSkillChangeNotifications = false;
            }

            await ReloadAsync();
            if (shouldClearSelection)
            {
                SelectedSkill = null;
                ClearStatus();
            }
            else
            {
                SetStatus($"Deleted skill '{displayName}'.", SkillStatusKind.Success, autoClear: true);
            }

            if (SelectedSkill is null)
            {
                IsDetailActive = false;
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SkillStatusKind.Error);
        }
    }

    private bool CanDeleteSelectedSkill() => SelectedSkill is not null && !IsBusy;

    [RelayCommand]
    private void BackToSkillList()
    {
        if (IsCompactLayout)
        {
            SelectedSkill = null;
            ClearStatus();
        }

        IsDetailActive = false;
    }

    [RelayCommand]
    private void NewSkill()
    {
        SelectedSkill = null;
        IsDetailActive = true;
        SetStatus("Import a skill from GitHub or select a local skill folder.", SkillStatusKind.Warning);
    }

    [RelayCommand]
    private void OpenSkillDetail(InstalledSkillItemViewModel? skill)
    {
        if (skill is not null)
        {
            ActivateSkill(skill);
            return;
        }

        IsDetailActive = true;
    }

    public void ActivateSkill(InstalledSkillItemViewModel skill)
    {
        if (!string.Equals(SelectedSkill?.SkillId, skill.SkillId, StringComparison.OrdinalIgnoreCase))
        {
            SelectedSkill = skill;
        }

        if (IsCompactLayout)
        {
            IsDetailActive = true;
        }
    }

    private async Task RunImportAsync(
        Func<CancellationToken, Task<IReadOnlyList<InstalledSkillRecord>>> action,
        string successMessage)
    {
        IsBusy = true;
        try
        {
            IReadOnlyList<InstalledSkillRecord> imported;
            _suppressSkillChangeNotifications = true;
            try
            {
                imported = await action(CancellationToken.None);
            }
            finally
            {
                _suppressSkillChangeNotifications = false;
            }

            if (imported.Count == 0)
            {
                await ReloadAsync();
                SetStatus("No skill folders were found to import.", SkillStatusKind.Warning);
                return;
            }

            await ReloadAsync(imported[0].SkillId);
            if (IsCompactLayout)
            {
                SelectedSkill = null;
                IsDetailActive = false;
                ClearStatus();
            }
            else
            {
                IsDetailActive = true;
                SetStatus(imported.Count == 1 ? successMessage : $"{successMessage} Imported {imported.Count} skills.", SkillStatusKind.Success, autoClear: true);
            }
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SkillStatusKind.Error);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ReloadAsync(string? selectSkillId = null)
    {
        var currentSkillId = SelectedSkill?.SkillId;
        var skills = await _gateway.ListAsync();
        SetSelectionSilently(() =>
        {
            Skills.Clear();
            foreach (var skill in skills)
            {
                Skills.Add(new InstalledSkillItemViewModel(skill, skill.RelativeRootPath));
            }

            var selectedSkill = Skills.FirstOrDefault(skill => string.Equals(skill.SkillId, selectSkillId, StringComparison.OrdinalIgnoreCase));
            if (selectedSkill is null && (!IsCompactLayout || selectSkillId is not null))
            {
                selectedSkill = Skills.FirstOrDefault(skill => string.Equals(skill.SkillId, currentSkillId, StringComparison.OrdinalIgnoreCase))
                                ?? Skills.FirstOrDefault();
            }

            SelectedSkill = selectedSkill;
        });
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            await ReloadAsync();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SkillStatusKind.Error);
        }

    }

    private void OnSkillsChanged()
        => RunOnUiThread(() =>
        {
            if (!_disposed && !_suppressSkillChangeNotifications)
            {
                _ = ReloadSafelyAsync(SelectedSkill?.SkillId);
            }
        });

    private async Task ReloadSafelyAsync(string? skillId)
    {
        try
        {
            await ReloadAsync(skillId);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SkillStatusKind.Error);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gateway.SkillsChanged -= OnSkillsChanged;
        _successStatus.Dispose();
        _tasks.Dispose();
    }

    private void ClearStatus()
        => SetStatus(string.Empty, SkillStatusKind.None);

    private void SetStatus(string message, SkillStatusKind kind, bool autoClear = false)
    {
        _successStatus.Cancel();
        StatusKind = string.IsNullOrWhiteSpace(message) ? SkillStatusKind.None : kind;
        StatusText = message;
        if (autoClear && StatusKind == SkillStatusKind.Success)
        {
            _tasks.Run(_ => _successStatus.ScheduleAsync(
                SuccessStatusDisplayDuration,
                () => RunOnUiThread(() =>
                {
                    if (StatusKind == SkillStatusKind.Success
                        && string.Equals(StatusText, message, StringComparison.Ordinal))
                    {
                        ClearStatus();
                    }
                })));
        }
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

    private void RunOnUiThread(Action action)
    {
        if (_uiDispatcher.CheckAccess())
        {
            action();
            return;
        }

        _tasks.Run(_ => _uiDispatcher.InvokeAsync(action));
    }
}

public enum SkillStatusKind
{
    None = 0,
    Success,
    Warning,
    Error,
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
