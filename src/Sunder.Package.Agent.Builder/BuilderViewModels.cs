using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.ExceptionServices;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

/// <summary>Activation-owned presentation and development sessions. Views only borrow this model.</summary>
public sealed partial class BuilderViewModel : BuilderObservable, IAsyncDisposable
{
    public const string BuilderPackageId = "younics.sunder.package.maker";
    private readonly BuilderProjectApplicationService _application;
    private readonly BuilderProjectPersistence _persistence;
    private readonly BuilderPathService _paths;
    private readonly IBuilderUiDispatcher _ui;
    private readonly IPackageDevelopmentService _development;
    private readonly IPackageLogNavigationService _logs;
    private readonly IPackageSettingsNavigationService _settings;
    private readonly PresentationTaskScope _tasks;
    private readonly KeyedAdaptiveListDetailState<string, BuilderProjectViewModel> _listDetail;
    private readonly ConcurrentDictionary<string, DevelopmentOwnership> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _operations = new(StringComparer.Ordinal);
    private readonly object _lifecycleSync = new();
    private Task? _initialization;
    private Task? _disposal;
    private bool _projectsLoaded;
    private volatile bool _stopping;
    private bool _disposed;
    private bool _showCreation;
    private bool _opening;
    private string _status = string.Empty;
    private string _persistenceError = string.Empty;
    private long _navigationRevision;
    private long _openGeneration;
    private long _wizardGeneration;
    private CancellationTokenSource? _wizardCancellation;
    private BuilderProjectViewModel? _locating;
    private BuilderProjectInspection? _selectedCandidate;

    public BuilderViewModel(BuilderProjectApplicationService applicationService, BuilderProjectPersistence persistence,
        BuilderPathService pathService, IBuilderUiDispatcher uiDispatcher, IPackageDevelopmentService development,
        IPackageLogNavigationService logs, IPackageSettingsNavigationService settings)
    {
        _application = applicationService;
        _persistence = persistence;
        _paths = pathService;
        _ui = uiDispatcher;
        _tasks = new(PresentationTaskScope.PropagateExpectedFailure, _ui.ShutdownToken);
        _development = development;
        _logs = logs;
        _settings = settings;
        Creation = new(pathService);
        _listDetail = new(Projects, static row => row.Id, static (_, _) => { }, StringComparer.Ordinal);
        _listDetail.PropertyChanged += OnRouteChanged;
        Projects.CollectionChanged += (_, _) => ChangedAll();
        _persistence.SaveFailed += OnPersistenceFailed;
        _persistence.SaveSucceeded += OnPersistenceSucceeded;
    }

    public ObservableCollection<BuilderProjectViewModel> Projects { get; } = [];
    public ObservableCollection<BuilderProjectInspection> OpenCandidates { get; } = [];
    public BuilderCreationViewModel Creation { get; }
    public BuilderProjectInspection? SelectedCandidate { get => _selectedCandidate; set => Set(ref _selectedCandidate, value); }
    public bool ShowCandidateChooser => OpenCandidates.Count > 0;
    public bool ShowCreation => _showCreation;
    public bool ShowProjects => !ShowCreation;
    public bool IsOpening => _opening;
    public bool IsEmpty => Projects.Count == 0;
    public bool HasSelectedProject => SelectedProject is not null;
    public string StatusText
    {
        get => string.Join(Environment.NewLine, new[] { _status, _persistenceError }.Where(message => message.Length > 0));
        private set { _status = value; ChangedAll(); }
    }
    public bool ShowStatusMessage => StatusText.Length > 0;
    public string RuntimeLogText => SelectedProject?.Output ?? string.Empty;
    public bool HasRuntimeLog => SelectedProject?.HasOutput == true;
    public bool IsCompactLayout
    {
        get => _listDetail.Layout == AdaptiveListDetailLayout.Compact;
        set => _listDetail.SetLayout(value ? AdaptiveListDetailLayout.Compact : AdaptiveListDetailLayout.Wide);
    }
    public bool IsEditorActive => IsCompactLayout && !_listDetail.IsList;
    public bool ShowListPane => !IsCompactLayout || !IsEditorActive;
    public bool ShowEditorPane => !IsCompactLayout || IsEditorActive;
    public bool ShowCompactList => IsCompactLayout && !IsEditorActive;
    public bool ShowCompactEditor => IsEditorActive;
    public BuilderProjectViewModel? SelectedProject
    {
        get => _listDetail.SelectedItem;
        set { if (value is null) BackToProjectList(); else ActivateProject(value); }
    }

    public void ActivateProject(BuilderProjectViewModel? project)
    {
        if (project is null || _stopping) return;
        _navigationRevision++;
        _openGeneration++;
        _showCreation = false;
        OpenCandidates.Clear();
        _listDetail.ShowExistingDetail(project);
        MarkDetailReady();
        ChangedAll();
        _ = RefreshSelectedProjectAsync();
    }

    public void BackToProjectList()
    {
        _navigationRevision++;
        _openGeneration++;
        _showCreation = false;
        OpenCandidates.Clear();
        _listDetail.ShowList();
        ChangedAll();
    }

    public Task CreateProjectAsync() => RunOwnedAsync(_ => UpdateUiAsync(() =>
    {
        if (Creation.IsBusy) { _showCreation = true; ChangedAll(); return; }
        _navigationRevision++;
        _wizardGeneration++;
        var last = Projects.OrderByDescending(row => row.ToRecord().UpdatedAtUtc)
            .Select(row => row.ProjectFolder).FirstOrDefault(Path.IsPathFullyQualified);
        Creation.Reset(last is null ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetDirectoryName(last) ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        _showCreation = true;
        OpenCandidates.Clear();
        ChangedAll();
    }));

    public void CancelCurrentOperation()
    {
        if (SelectedProject is { } row && _operations.TryGetValue(row.Id, out var cancellation)) cancellation.Cancel();
    }

    public void CancelCreation() => _wizardCancellation?.Cancel();
    public void BackToTechnology() { if (!Creation.IsBusy) Creation.Step = 0; }
    public void DiscardSelectedMetadata() => SelectedProject?.DiscardEdits();

    public Task OpenPackageLogsAsync()
    {
        var id = SelectedProject?.PackageId;
        return RunOwnedAsync(async token =>
        {
            if (!string.IsNullOrEmpty(id)) await _logs.OpenPackageLogsAsync(id, token).ConfigureAwait(false);
        });
    }

    public Task OpenSdkSettingsAsync() => RunOwnedAsync(async token =>
    {
        if (!await _settings.OpenPackageSettingsAsync(BuilderPackageId, cancellationToken: token).ConfigureAwait(false))
            await UpdateUiAsync(() => StatusText = "SDK settings are unavailable in this host.").ConfigureAwait(false);
    });

    private void OnRouteChanged(object? sender, PropertyChangedEventArgs e) { MarkDetailReady(); ChangedAll(); }
    private void MarkDetailReady()
    {
        if (_listDetail.IsExistingDetail && _listDetail.DetailPhase == AdaptiveDetailPhase.None)
            _listDetail.TrySetDetailReady(_listDetail.BeginDetailLoad(_tasks.CancellationToken));
    }

    private bool IsCurrent(BuilderProjectViewModel row, long generation)
        => !_disposed && Projects.Contains(row) && row.OperationGeneration == generation;

    private void SaveBookmarks()
    {
        if (_projectsLoaded && !_disposed) _persistence.RequestSave(Projects.Select(row => row.ToRecord()).ToArray());
    }

    private void OnPersistenceFailed(object? sender, BuilderPersistenceFailedEventArgs e)
        => _tasks.Run(_ => UpdateUiAsync(() => { _persistenceError = "Project bookmarks could not be saved: " + e.Exception.Message; ChangedAll(); }));

    private void OnPersistenceSucceeded(object? sender, EventArgs e)
        => _tasks.Run(_ => UpdateUiAsync(() => { _persistenceError = string.Empty; ChangedAll(); }));

    private Task RunOwnedAsync(Func<CancellationToken, Task> action)
        => _tasks.Track(token => Task.Run(async () =>
        {
            token.ThrowIfCancellationRequested();
            try { await action(token).ConfigureAwait(false); }
            catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, token)) { }
            catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
            {
                await UpdateUiAsync(() => { if (!_disposed) StatusText = error.Message; }).ConfigureAwait(false);
            }
        }));

    // UI mutations may be abandoned only for this activation's stop or proven dispatcher shutdown.
    // Queries must throw instead: a fabricated default could acquire/release the wrong session.
    private async Task UpdateUiAsync(Action action)
    {
        try { await _ui.InvokeAsync(action, _tasks.CancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (PresentationCancellation.IsExpected(error, _tasks.CancellationToken)) { }
    }

    private Task<T> ReadUiAsync<T>(Func<T> action)
        => _ui.InvokeAsync(action, _tasks.CancellationToken);

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleSync)
        {
            _stopping = true;
            return new(_disposal ??= DisposeCoreAsync());
        }
    }

    private async Task DisposeCoreAsync()
    {
        // These used to be cancelled only after draining _tasks, while a retained monitor was
        // still calling GetStatus and posting UI. Link them to the activation before starting.
        // Cancel all producers first; release/worker cleanup below never needs the dispatcher.
        List<Exception> errors = [];
        try { _tasks.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        // A cancellation callback may fail after cancellation was requested. Still drain every
        // accepted operation before releasing the sessions and persistence it may be using.
        await CaptureCleanupFailureAsync(errors, () => _tasks.DisposeAsync().AsTask()).ConfigureAwait(false);
        foreach (var owner in _sessions.Values)
        {
            await CaptureCleanupFailureAsync(errors, () => ReleaseSessionAsync(owner)).ConfigureAwait(false);
        }
        _persistence.SaveFailed -= OnPersistenceFailed;
        _persistence.SaveSucceeded -= OnPersistenceSucceeded;
        await CaptureCleanupFailureAsync(errors, () => _persistence.DisposeAsync().AsTask()).ConfigureAwait(false);
        _disposed = true;
        _listDetail.PropertyChanged -= OnRouteChanged;
        try { _listDetail.Dispose(); }
        catch (Exception error) { errors.Add(error); }
        if (errors.Count > 0) throw new AggregateException("Builder activation cleanup failed.", errors);
    }

    private static async Task CaptureCleanupFailureAsync(List<Exception> errors, Func<Task> cleanup)
    {
        try { await cleanup().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }

    private static void ThrowCleanupFailures(List<Exception> errors)
    {
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Builder development session cleanup failed.", errors);
    }

    private sealed class DevelopmentOwnership(BuilderProjectViewModel row, IPackageDevelopmentSession handle, CancellationToken lifetime)
    {
        public BuilderProjectViewModel Row { get; } = row;
        public IPackageDevelopmentSession Handle { get; } = handle;
        public string SourcePackageId { get; } = row.PackageId;
        public string? SourceEntryPoint { get; } = row.EntryPoint;
        public BuilderSourceChangeMonitor? SourceChanges { get; set; }
        public CancellationTokenSource? WatchCancellation { get; set; }
        public Task? WatchTask { get; set; }
        public string? WatchJobId { get; set; }
        public bool WatchStopConfirmed { get; set; } = true;
        public bool ReleaseStarted { get; set; }
        public CancellationTokenSource MonitorCancellation { get; } = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        public Task MonitorTask { get; set; } = Task.CompletedTask;
    }
}
