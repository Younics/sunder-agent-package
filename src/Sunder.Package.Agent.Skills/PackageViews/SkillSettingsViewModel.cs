using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Package.Agent.Skills.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.PackageViews;

public sealed partial class SkillSettingsViewModel : ObservableObject,
    IPackageViewNavigationPreparationTarget,
    IDisposable
{
    private static readonly TimeSpan SuccessStatusDisplayDuration = TimeSpan.FromSeconds(3);
    private const string ListRefreshChannel = "skills-list";
    private const string MutationChannel = "skills-mutation";

    private readonly ISkillManagementGateway _gateway;
    private readonly TimedStatusController _successStatus = new();
    private readonly IPresentationDispatcher _uiDispatcher = PresentationDispatcher.Capture();
    private readonly PresentationTaskScope _tasks = new();
    private readonly LatestRequestCoordinator _requests = new();
    private readonly KeyedAdaptiveListDetailState<string, InstalledSkillItemViewModel> _listDetail;
    private readonly SerializedRefreshLoop _runtimeRefresh;
    private readonly Task _initialization;
    private bool _disposed;

    internal static IReadOnlyCollection<string> OwnedConfigurationKeys { get; } = [];

    public SkillSettingsViewModel(SkillStore store, SkillImportService importService)
        : this(new SkillLocalManagementGateway(store, importService))
    {
    }

    internal SkillSettingsViewModel(ISkillManagementGateway gateway)
    {
        _gateway = gateway;
        _listDetail = new KeyedAdaptiveListDetailState<string, InstalledSkillItemViewModel>(
            Skills,
            static skill => skill.SkillId,
            static (current, incoming) => current.Apply(incoming),
            StringComparer.OrdinalIgnoreCase);
        _listDetail.PropertyChanged += OnListDetailPropertyChanged;
        _runtimeRefresh = new SerializedRefreshLoop(
            RefreshListAsync,
            exception => RunOnUiThread(() => SetStatus(exception.Message, SkillStatusKind.Error)));
        _gateway.SkillsChanged += OnSkillsChanged;
        _initialization = InitializeCoreAsync();
    }

    public ObservableCollection<InstalledSkillItemViewModel> Skills { get; } = [];

    public bool HasSelectedSkill => SelectedSkill is not null;

    public bool IsListActive => _listDetail.IsList;

    public bool ShowWideLayout => _listDetail.Layout == AdaptiveListDetailLayout.Wide;

    public bool ShowCompactList => IsCompactLayout && IsListActive;

    public bool ShowCompactDetail => IsCompactLayout && IsDetailActive;

    public bool ShowListPane => ShowWideLayout || ShowCompactList;

    public bool ShowDetailPane => ShowWideLayout || ShowCompactDetail;

    public InstalledSkillItemViewModel? SelectedSkill
    {
        get => _listDetail.SelectedItem;
        set
        {
            if (value is null)
            {
                ShowListFromUserIntent();
            }
            else
            {
                ShowExistingFromUserIntent(value);
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

    public bool IsDetailActive
    {
        get => !_listDetail.IsList;
        set
        {
            if (!value)
            {
                ShowListFromUserIntent();
            }
            else if (SelectedSkill is { } selected)
            {
                ShowExistingFromUserIntent(selected);
            }
            else
            {
                BeginUserIntent();
                _listDetail.ShowNewDetail();
            }
        }
    }

    internal AdaptiveListDetailRoute Route => _listDetail.Route;

    internal AdaptiveListDetailLayout Layout => _listDetail.Layout;

    internal AdaptiveDetailPhase DetailPhase => _listDetail.DetailPhase;

    internal long IntentRevision => _listDetail.IntentRevision;

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

    public async ValueTask<bool> PrepareNavigationAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        await _initialization.WaitAsync(cancellationToken);
        return true;
    }

    public ValueTask OnNavigationPresentedAsync(
        PackageViewNavigationContext context,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    internal Task RuntimeRefreshIdle => _runtimeRefresh.WhenIdle;

    partial void OnIsBusyChanged(bool value)
        => DeleteSelectedSkillCommand.NotifyCanExecuteChanged();

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
        var selected = SelectedSkill;
        if (selected is null)
        {
            return;
        }

        var intentRevision = IntentRevision;
        var mutation = _requests.Begin(MutationChannel);
        IsBusy = true;
        try
        {
            var displayName = selected.DisplayName;
            var deletedSkillId = selected.SkillId;
            await _gateway.DeleteAsync(deletedSkillId, mutation.CancellationToken);
            if (!_requests.IsCurrent(mutation)
                || intentRevision != IntentRevision)
            {
                return;
            }

            DiscardPendingSkillRefresh();
            if (IsCompactLayout)
            {
                _listDetail.ShowList();
                ClearStatus();
            }
            else
            {
                SetStatus($"Deleted skill '{displayName}'.", SkillStatusKind.Success, autoClear: true);
            }

            _listDetail.Reconcile(
                Skills.Where(skill => !string.Equals(
                    skill.SkillId,
                    deletedSkillId,
                    StringComparison.OrdinalIgnoreCase)).ToArray());
            _tasks.Run(_runtimeRefresh.MarkDirty());
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_requests.IsCurrent(mutation))
            {
                SetStatus(ex.Message, SkillStatusKind.Error);
            }
        }
        finally
        {
            if (_requests.Complete(mutation))
            {
                IsBusy = false;
            }
        }
    }

    private bool CanDeleteSelectedSkill() => SelectedSkill is not null && !IsBusy;

    [RelayCommand]
    private void BackToSkillList()
    {
        ShowListFromUserIntent();
        if (IsCompactLayout)
        {
            ClearStatus();
        }
    }

    [RelayCommand]
    private void NewSkill()
    {
        BeginUserIntent();
        _listDetail.ShowNewDetail();
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

        BeginUserIntent();
        _listDetail.ShowNewDetail();
    }

    public void ActivateSkill(InstalledSkillItemViewModel skill)
    {
        ShowExistingFromUserIntent(skill);
    }

    private async Task RunImportAsync(
        Func<CancellationToken, Task<IReadOnlyList<InstalledSkillRecord>>> action,
        string successMessage)
    {
        BeginUserIntent();
        var intentRevision = _listDetail.ShowNewDetail();
        var mutation = _requests.Begin(MutationChannel);
        IsBusy = true;
        try
        {
            var imported = await action(mutation.CancellationToken);

            if (imported.Count == 0)
            {
                await RefreshListAsync(mutation.CancellationToken);
                if (_requests.IsCurrent(mutation))
                {
                    SetStatus("No skill folders were found to import.", SkillStatusKind.Warning);
                }
                return;
            }

            DiscardPendingSkillRefresh();
            var snapshot = await _gateway.ListAsync(mutation.CancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (!_requests.IsCurrent(mutation))
                {
                    return;
                }

                _listDetail.Reconcile(snapshot.Select(static skill =>
                    new InstalledSkillItemViewModel(skill, skill.RelativeRootPath)).ToArray());
                if (_listDetail.TryShowCreatedDetail(imported[0].SkillId, intentRevision))
                {
                    if (IsCompactLayout)
                    {
                        _listDetail.ShowList();
                        ClearStatus();
                    }
                    else
                    {
                        MarkCurrentDetailReady();
                        SetStatus(
                            imported.Count == 1
                                ? successMessage
                                : $"{successMessage} Imported {imported.Count} skills.",
                            SkillStatusKind.Success,
                            autoClear: true);
                    }
                }
            });
        }
        catch (OperationCanceledException) when (mutation.CancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            if (_requests.IsCurrent(mutation))
            {
                SetStatus(ex.Message, SkillStatusKind.Error);
            }
        }
        finally
        {
            if (_requests.Complete(mutation))
            {
                IsBusy = false;
            }
        }
    }

    private async Task RefreshListAsync(CancellationToken cancellationToken)
    {
        var request = _requests.Begin(ListRefreshChannel, cancellationToken);
        try
        {
            var skills = await _gateway.ListAsync(request.CancellationToken);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_requests.IsCurrent(request))
                {
                    _listDetail.Reconcile(skills.Select(static skill =>
                        new InstalledSkillItemViewModel(skill, skill.RelativeRootPath)).ToArray());
                }
            });
        }
        catch (OperationCanceledException) when (request.CancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _requests.Complete(request);
        }
    }

    private async Task InitializeCoreAsync()
    {
        try
        {
            await RefreshListAsync(CancellationToken.None);
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, SkillStatusKind.Error);
        }

    }

    private void OnSkillsChanged()
        => _tasks.Run(_runtimeRefresh.MarkDirty());

    private void DiscardPendingSkillRefresh()
    {
        _runtimeRefresh.DiscardPending();
        _requests.Invalidate(ListRefreshChannel);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _gateway.SkillsChanged -= OnSkillsChanged;
        _listDetail.PropertyChanged -= OnListDetailPropertyChanged;
        _runtimeRefresh.Dispose();
        _listDetail.Dispose();
        _requests.Dispose();
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

    private void BeginUserIntent()
    {
        _requests.Invalidate(MutationChannel);
        IsBusy = false;
    }

    private void ShowListFromUserIntent()
    {
        BeginUserIntent();
        _listDetail.ShowList();
    }

    private void ShowExistingFromUserIntent(InstalledSkillItemViewModel skill)
    {
        BeginUserIntent();
        _listDetail.ShowExistingDetail(skill);
        MarkCurrentDetailReady();
    }

    private void MarkCurrentDetailReady()
    {
        if (_listDetail.IsExistingDetail && _listDetail.DetailPhase == AdaptiveDetailPhase.None)
        {
            var ticket = _listDetail.BeginDetailLoad();
            _listDetail.TrySetDetailReady(ticket);
        }
    }

    private void OnListDetailPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(KeyedAdaptiveListDetailState<string, InstalledSkillItemViewModel>.SelectedItem))
        {
            OnPropertyChanged(nameof(SelectedSkill));
            OnPropertyChanged(nameof(HasSelectedSkill));
            DeleteSelectedSkillCommand.NotifyCanExecuteChanged();
            MarkCurrentDetailReady();
        }

        OnPropertyChanged(nameof(IsListActive));
        OnPropertyChanged(nameof(IsDetailActive));
        OnPropertyChanged(nameof(IsCompactLayout));
        OnPropertyChanged(nameof(ShowWideLayout));
        OnPropertyChanged(nameof(ShowCompactList));
        OnPropertyChanged(nameof(ShowCompactDetail));
        OnPropertyChanged(nameof(ShowListPane));
        OnPropertyChanged(nameof(ShowDetailPane));
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

public sealed class InstalledSkillItemViewModel : ObservableObject
{
    private string _displayName = string.Empty;
    private string _description = string.Empty;
    private string _version = string.Empty;
    private string _author = string.Empty;
    private string _source = string.Empty;
    private string _resolvedCommitSha = string.Empty;
    private string _rootPath = string.Empty;
    private string _metadataText = string.Empty;
    private string _warningsText = string.Empty;

    public InstalledSkillItemViewModel(InstalledSkillRecord skill, string rootPath)
    {
        SkillId = skill.SkillId;
        Apply(skill, rootPath);
    }

    internal void Apply(InstalledSkillItemViewModel incoming)
    {
        if (!string.Equals(SkillId, incoming.SkillId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Cannot reconcile skill rows with different keys.");
        }

        DisplayName = incoming.DisplayName;
        Description = incoming.Description;
        Version = incoming.Version;
        Author = incoming.Author;
        Source = incoming.Source;
        ResolvedCommitSha = incoming.ResolvedCommitSha;
        RootPath = incoming.RootPath;
        MetadataText = incoming.MetadataText;
        WarningsText = incoming.WarningsText;
    }

    private void Apply(InstalledSkillRecord skill, string rootPath)
    {
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

    public string DisplayName
    {
        get => _displayName;
        private set => SetProperty(ref _displayName, value);
    }

    public string Description
    {
        get => _description;
        private set => SetProperty(ref _description, value);
    }

    public string Version
    {
        get => _version;
        private set => SetProperty(ref _version, value);
    }

    public string Author
    {
        get => _author;
        private set => SetProperty(ref _author, value);
    }

    public string Source
    {
        get => _source;
        private set => SetProperty(ref _source, value);
    }

    public string ResolvedCommitSha
    {
        get => _resolvedCommitSha;
        private set => SetProperty(ref _resolvedCommitSha, value);
    }

    public string RootPath
    {
        get => _rootPath;
        private set => SetProperty(ref _rootPath, value);
    }

    public string MetadataText
    {
        get => _metadataText;
        private set => SetProperty(ref _metadataText, value);
    }

    public string WarningsText
    {
        get => _warningsText;
        private set => SetProperty(ref _warningsText, value);
    }
}
