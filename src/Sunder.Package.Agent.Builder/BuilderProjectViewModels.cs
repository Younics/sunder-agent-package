using System.ComponentModel;
using System.Runtime.CompilerServices;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

public abstract class BuilderObservable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
    protected void ChangedAll() => Changed(string.Empty);
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class BuilderProjectViewModel : BuilderObservable
{
    public const int MaximumOutputCharacters = 128 * 1024;
    private BuilderProjectRecord _record;
    private BuilderProjectInspection? _inspection;
    private BuilderProjectInspection? _latestInspection;
    private string _editedDisplayName;
    private string _editedPackageId;
    private string _statusText = "Ready to load";
    private string _error = string.Empty;
    private string _sourceError = string.Empty;
    private string _output = string.Empty;
    private bool _busy;
    private bool _missing;
    private bool _conflict;
    private bool _hasSession;
    private bool _releasePending;
    private PackageDevelopmentStatus? _developmentStatus;
    private BuilderJobPhase _phase;
    private string _requestedOperation = "operation";
    private string _snapshotState = string.Empty;
    private DateTimeOffset? _lastSourceChangeAtUtc;
    private DateTimeOffset? _lastSuccessfulBuildAtUtc;
    private PackageDevelopmentRevision? _lastAppliedRevision;
    private DateTimeOffset? _lastAppliedObservedAtUtc;
    private bool _outputExpanded;
    private readonly TimeProvider _time;
    private long? _appPendingStarted;

    public BuilderProjectViewModel(BuilderProjectRecord record) : this(record, TimeProvider.System) { }

    internal BuilderProjectViewModel(BuilderProjectRecord record, TimeProvider time)
    {
        _time = time;
        _record = record;
        _editedDisplayName = record.DisplayName;
        _editedPackageId = record.PackageId;
        _missing = string.IsNullOrEmpty(record.EntryPoint);
    }

    public string Id => _record.Id;
    public string DisplayName => _record.DisplayName;
    public string PackageId => _record.PackageId;
    public string ProjectFolder => _record.ProjectFolder;
    public string? EntryPoint => _record.EntryPoint;
    public BuilderProjectKind? Kind => _record.Kind;
    public string Technology => Kind switch { BuilderProjectKind.Dotnet => ".NET · Avalonia", BuilderProjectKind.Node => "Node.js · React / TypeScript", _ => "Locate project" };
    public BuilderProjectInspection? Inspection => _inspection;
    public BuilderToolchainInstallation? Installation => _record.Installation;
    public bool SetupIncomplete => _record.SetupIncomplete;
    public bool IsMissing => _missing;
    public bool HasSourceConflict => _conflict;
    public bool HasSession => _hasSession;
    private bool _hotreloadPending;
    private BuilderReloadProgress? _reloadProgress;
    private string _observationError = string.Empty;
    internal void SetReloadProgress(BuilderReloadProgress? progress) { _reloadProgress = progress; ChangedAll(); }
    internal void SetObservationError(string? message) { _observationError = message ?? string.Empty; ChangedAll(); }
    public bool HotreloadPending => _hotreloadPending;
    internal void SetHotreloadPending(bool value) { _hotreloadPending = value; ChangedAll(); }
    public bool IsLoaded => _developmentStatus?.IsLoaded == true;
    public bool IsBusy { get => _busy; internal set { if (Set(ref _busy, value)) ChangedAll(); } }
    public bool CanOperate => !IsBusy && !IsMissing && Inspection is not null && !HotreloadPending;
    public bool CanHotreload => !IsBusy && _reloadProgress?.State != "running" && HasSession && !_releasePending && (Inspection is not null || HotreloadPending);
    public string HotreloadActionText => HotreloadPending ? "Resolve Hotreload" : "Hotreload Package";
    public bool CanLoad => CanOperate && !HasSession;
    public bool CanUnload => !IsBusy && HasSession;
    public bool CanEditDisplayName => CanOperate && (_latestInspection ?? Inspection)?.CanEditDisplayName == true;
    public bool CanEditPackageId => CanOperate && (_latestInspection ?? Inspection)?.CanEditPackageId == true;
    public string DisplayNameEditReason => (_latestInspection ?? Inspection)?.ReadOnlyReason ?? (IsMissing ? "Locate this project's source to edit its metadata." : string.Empty);
    public string PackageIdEditReason => (_latestInspection ?? Inspection)?.PackageIdReadOnlyReason ?? DisplayNameEditReason;
    public bool IsDirty => _editedDisplayName != (_inspection?.DisplayName ?? DisplayName) || _editedPackageId != (_inspection?.PackageId ?? PackageId);
    public bool CanSave => CanOperate && IsDirty && !HasSourceConflict && !string.IsNullOrWhiteSpace(EditedDisplayName)
        && Sunder.Sdk.Packaging.PackageId.TryParse(EditedPackageId, out _)
        && (EditedDisplayName == Inspection?.DisplayName || CanEditDisplayName)
        && (EditedPackageId == Inspection?.PackageId || CanEditPackageId);
    public string EditedDisplayName { get => _editedDisplayName; set { if (Set(ref _editedDisplayName, value ?? string.Empty)) ChangedAll(); } }
    public string EditedPackageId { get => _editedPackageId; set { if (Set(ref _editedPackageId, value ?? string.Empty)) ChangedAll(); } }
    public string StatusText => _statusText;
    public string ErrorMessage => string.Join(Environment.NewLine, new[] { _sourceError, _error, _observationError,
        _reloadProgress?.State is "unknown" or "failed" ? _reloadProgress.Error ?? string.Empty : string.Empty }.Where(message => message.Length > 0).Distinct());
    public bool HasError => ErrorMessage.Length > 0;
    public string Output => _output;
    public bool HasOutput => _output.Length > 0;
    public bool IsOutputExpanded { get => _outputExpanded; set => Set(ref _outputExpanded, value); }
    public string? ArchivePath { get; private set; }
    public bool HasArchive => !string.IsNullOrEmpty(ArchivePath);
    public string LoadStateText => _releasePending ? "Release unconfirmed" : !HasSession ? "Unloaded"
        : _reloadProgress?.State == "unknown" ? "Reload unconfirmed" : _reloadProgress?.State == "running" ? "Reload pending" : !IsLoaded ? "Connecting…"
        : _developmentStatus?.AppReadiness switch
        {
            PackageDevelopmentAppReadiness.Pending => "Runtime loaded · UI pending",
            PackageDevelopmentAppReadiness.Quarantined => "Runtime loaded · UI failed",
            PackageDevelopmentAppReadiness.Unavailable => "Runtime loaded · UI unavailable",
            PackageDevelopmentAppReadiness.NotApplicable => "Loaded (Runtime only)",
            PackageDevelopmentAppReadiness.Activated => "Loaded",
            _ => "Runtime loaded · UI unknown",
        };
    private bool IsAppActivationDelayed => _appPendingStarted is { } started
        && _time.GetElapsedTime(started) >= TimeSpan.FromSeconds(15);
    public string StateText => IsBusy ? _requestedOperation switch
        {
            "load" => "Loading…", "unload" => "Unloading…", "build" => "Building…", "hotreload" => "Hotreloading…", "archive" => "Creating archive…",
            "save-metadata" => "Saving…", "setup" => "Setting up…", _ => "Updating…",
        }
        : IsMissing ? "Folder missing" : HasSession ? LoadStateText
        : HasError ? "Needs attention" : SetupIncomplete ? "Setup required" : "Unloaded";
    public bool IsBuildRunning => IsBusy && _snapshotState == "running"
        && _phase is BuilderJobPhase.Restoring or BuilderJobPhase.Building or BuilderJobPhase.Validating or BuilderJobPhase.Packaging;
    public string BuildActionText => "Build Package";
    public string ActivityText => IsBuildRunning ? _phase switch
        {
            BuilderJobPhase.Restoring => "Restoring dependencies…", BuilderJobPhase.Validating => "Validating package output…",
            BuilderJobPhase.Packaging => "Checking contracts and creating archive…", _ => "Building package…",
        }
        : _reloadProgress?.State is "running" or "unknown" ? _reloadProgress.Phase switch
        {
            "loading" => "Loading the selected output…",
            "reloading" => _reloadProgress.State == "unknown" ? "Reload outcome unconfirmed. Resolve the original Hotreload; do not rebuild or load another owner." : "Retiring the old UI and committing the selected output…",
            "waiting-for-app" => "Waiting for the exact Runtime/App revision to be confirmed applied.",
            _ => "Observing the original package operation…",
        }
        : IsBusy ? StateText
        : _releasePending ? "Owner release is unconfirmed. Reconnect and retry Unload."
        : HasSession && !IsLoaded ? "Waiting for the Runtime…"
        : HasSession && _developmentStatus?.AppReadiness == PackageDevelopmentAppReadiness.Quarantined ? "Package UI activation failed. Open package logs for details."
        : HasSession && _developmentStatus?.AppReadiness == PackageDevelopmentAppReadiness.Unavailable ? "Package UI readiness is unavailable. Open package logs for details."
        : HasSession && _developmentStatus?.AppReadiness == PackageDevelopmentAppReadiness.Pending
            ? IsAppActivationDelayed ? "Package UI activation is taking longer than expected. Open package logs for details." : "Activating package UI…"
        : HasSession ? "Use Build Package to compile, or Hotreload Package to apply a new revision."
        : "Load this project to run it in Sunder.";
    public DateTimeOffset? LastSourceChangeAtUtc => _lastSourceChangeAtUtc;
    public DateTimeOffset? LastSuccessfulBuildAtUtc => _lastSuccessfulBuildAtUtc;
    public string SourceActivityText => _lastSourceChangeAtUtc is { } changed ? $"Last source change · {changed.ToLocalTime():HH:mm:ss}" : "No source changes observed in this session";
    public string BuildActivityText => _lastSuccessfulBuildAtUtc is { } built ? $"Last successful build · {built.ToLocalTime():HH:mm:ss}" : string.Empty;
    public bool HasBuildActivity => _lastSuccessfulBuildAtUtc is not null;
    public PackageDevelopmentRevision? LastAppliedRevision => _lastAppliedRevision;
    public DateTimeOffset? LastAppliedObservedAtUtc => _lastAppliedObservedAtUtc;
    public bool HasAppliedActivity => _lastAppliedRevision is not null;
    public string AppliedActivityText => _lastAppliedRevision is { } applied && _lastAppliedObservedAtUtc is { } observed
        ? $"Last applied output · {applied.SourceContentIdentity[..12]} · observed {observed.ToLocalTime():HH:mm:ss}"
        : "No applied output revision observed";
    public string ApplicationStatusText => !HasSession ? string.Empty
        : _releasePending ? "Development owner release is not yet confirmed. Last applied output is historical."
        : _reloadProgress?.State is "running" or "unknown" ? "The selected reload is not yet confirmed applied. Last applied output is historical."
        : _developmentStatus?.AppReadiness == PackageDevelopmentAppReadiness.Quarantined ? "Runtime output is loaded, but App UI activation failed. Open package logs for details."
        : _developmentStatus?.AppReadiness == PackageDevelopmentAppReadiness.Unavailable ? "App UI activation cannot currently be observed. Applied output is not confirmed."
        : _developmentStatus?.RuntimeRevision is null ? "This Host has not supplied an applied-output revision."
        : _developmentStatus.AppliedRevision is null ? "Current Runtime output is not yet confirmed applied."
        : _developmentStatus.AppReadiness == PackageDevelopmentAppReadiness.NotApplicable ? "Runtime matches this output revision; no App UI is required."
        : "Runtime and App activation match this output revision.";
    public bool HasDisplayNameEditReason => DisplayNameEditReason.Length > 0;
    public bool HasPackageIdEditReason => PackageIdEditReason.Length > 0;
    public string SdkText => Installation is { } sdk
        ? $"{(sdk.Kind == BuilderProjectKind.Dotnet ? ".NET SDK" : "Node.js")} {sdk.Version} · {(sdk.IsManaged ? "Builder managed" : "External")}"
        : "SDK checked when you set up or load this project.";
    public string RuntimeStatusText => _developmentStatus is { } state && HasSession
        ? $"Host: {ReadinessText(state.HostState)} · Runtime: {ReadinessText(state.RuntimeReadiness)} · App: {ReadinessText(state.AppReadiness)}"
          + (state.OverridesInstalledPackage ? " · Overrides installed package" : string.Empty)
        : HasSession ? "Checking runtime and App readiness…" : "This Builder session is stopped.";
    private static string ReadinessText<T>(T value) where T : struct, Enum
        => Enum.IsDefined(value) ? value.ToString() : "Unknown";
    public string RuntimeError => _developmentStatus?.ErrorMessage ?? string.Empty;
    public bool HasRuntimeError => RuntimeError.Length > 0;

    internal long OperationGeneration { get; set; }

    public BuilderProjectRecord ToRecord() => _record;

    internal void ApplyInspection(BuilderProjectInspection value, bool acceptEdits = false)
    {
        var dirty = IsDirty;
        _latestInspection = value;
        if (!dirty || acceptEdits || _inspection is null)
        {
            _inspection = value;
            _editedDisplayName = value.DisplayName;
            _editedPackageId = value.PackageId;
            _conflict = false;
        }
        else if (_inspection.SourceRevision != value.SourceRevision) _conflict = true;
        _record = _record with
        {
            DisplayName = value.DisplayName, PackageId = value.PackageId, ProjectFolder = value.ProjectFolder,
            EntryPoint = value.EntryPoint, Kind = value.Kind,
        };
        _missing = false;
        _sourceError = string.Empty;
        ChangedAll();
    }

    public void DiscardEdits()
    {
        if (_latestInspection is { } latest) ApplyInspection(latest, acceptEdits: true);
        else { _editedDisplayName = DisplayName; _editedPackageId = PackageId; _conflict = false; ChangedAll(); }
    }

    internal void ApplySnapshot(BuilderJobSnapshot snapshot, bool acceptEdits = false)
    {
        _phase = snapshot.Phase;
        _snapshotState = snapshot.State;
        if (snapshot.LastSourceChangeAtUtc is { } changed) ObserveSourceChange(changed);
        if (snapshot.LastSuccessfulBuildAtUtc is { } built && (_lastSuccessfulBuildAtUtc is null || built > _lastSuccessfulBuildAtUtc))
        {
            _lastSuccessfulBuildAtUtc = built;
        }
        if (snapshot.Project is { } project) ApplyInspection(project, acceptEdits);
        if (snapshot.Installation is { } installation) _record = _record with { Installation = installation };
        if (snapshot.ArchivePath is { } archive) ArchivePath = archive;
        _statusText = snapshot.ConnectionState == BuilderJobConnectionState.Reconnecting
            ? "Reconnecting to the same Builder job…" : snapshot.Message;
        _output = BoundOutput(snapshot.Output);
        if (snapshot.State == "failed") IsOutputExpanded = true;
        if (snapshot.State == "failed") _error = snapshot.Error ?? snapshot.Message;
        else if (snapshot.State == "succeeded") _error = string.Empty;
        if (snapshot.Operation is "create" or "setup" && snapshot.State != "running")
            _record = _record with { SetupIncomplete = snapshot.State != "succeeded" };
        // Validated-build evidence is independent of Runtime/App application.
        if (snapshot.DevelopmentFolder is not null && (snapshot.State == "succeeded"
            || snapshot.State == "running" && snapshot.LastSuccessfulBuildAtUtc is not null
                && snapshot.Phase == BuilderJobPhase.BuildReady))
            _record = _record with { SetupIncomplete = false };
        _record = _record with { UpdatedAtUtc = DateTimeOffset.UtcNow };
        ChangedAll();
    }

    internal void SetMissing(string reason) { _missing = true; _sourceError = reason; ChangedAll(); }
    internal void BeginOperation(string operation)
    {
        _requestedOperation = operation;
        _phase = BuilderJobPhase.Queued;
        _snapshotState = string.Empty;
        _statusText = _output = _error = string.Empty;
        IsOutputExpanded = false;
    }
    internal void ObserveSourceChange(DateTimeOffset value)
    {
        if (_lastSourceChangeAtUtc is { } previous && value <= previous) return;
        _lastSourceChangeAtUtc = value;
        ChangedAll();
    }
    internal void SetError(string message) { _error = message; ChangedAll(); }
    internal void SetError(Exception error)
    {
        _output = BuilderJobText.Safe(_output + $"\n[{_requestedOperation}] App operation failed:\n" + error + "\n", MaximumOutputCharacters);
        IsOutputExpanded = true;
        SetError(BuilderJobText.Safe(error.Message, 8192));
    }
    internal void SetStatus(string message) { _statusText = message; ChangedAll(); }
    internal void SetSession(bool hasSession)
    {
        if (!hasSession) _appPendingStarted = null;
        _hasSession = hasSession;
        if (!hasSession) { _developmentStatus = null; _releasePending = false; _reloadProgress = null; }
        ChangedAll();
    }
    internal void SetReleasePending()
    {
        _releasePending = true;
        _developmentStatus = new(PackageId, false, false, PackageDevelopmentRuntimeReadiness.Unknown, null, false)
        { HostState = PackageDevelopmentHostState.Unknown, AppReadiness = PackageDevelopmentAppReadiness.Unknown };
        // LastAppliedRevision/observed time intentionally remain historical evidence.
        ChangedAll();
    }
    internal void SetDevelopmentStatus(PackageDevelopmentStatus status)
    {
        if (status.IsLoaded && status.AppReadiness == PackageDevelopmentAppReadiness.Pending)
        {
            if (_appPendingStarted is null || status.RuntimeRevision?.RuntimeInstanceId != _developmentStatus?.RuntimeRevision?.RuntimeInstanceId
                || status.RuntimeRevision?.SourceContentIdentity != _developmentStatus?.RuntimeRevision?.SourceContentIdentity)
                _appPendingStarted = _time.GetTimestamp();
        }
        else _appPendingStarted = null;
        _developmentStatus = status;
        if (status.AppliedRevision is { } applied)
        {
            if (_lastAppliedRevision is null || _lastAppliedRevision.RuntimeInstanceId != applied.RuntimeInstanceId
                || _lastAppliedRevision.SourceContentIdentity != applied.SourceContentIdentity)
                _lastAppliedObservedAtUtc = DateTimeOffset.UtcNow;
            _lastAppliedRevision = applied;
        }
        ChangedAll();
    }
    internal static string BoundOutput(string value) => value.Length <= MaximumOutputCharacters ? value : value[^MaximumOutputCharacters..];
}

/// <summary>Ephemeral wizard input. Name suggestions stop independently after each field is edited.</summary>
public sealed class BuilderCreationViewModel(BuilderPathService paths) : BuilderObservable
{
    private string _name = string.Empty;
    private string _packageId = string.Empty;
    private string _parent = string.Empty;
    private string _folder = string.Empty;
    private bool _idOverridden;
    private bool _folderOverridden;
    private int _step;
    private bool _busy;
    private string _message = string.Empty;
    private string _error = string.Empty;
    private string _output = string.Empty;
    public BuilderProjectKind? Kind { get; internal set; }
    public BuilderToolchainInstallation? Installation { get; internal set; }
    public int Step { get => _step; internal set { _step = value; ChangedAll(); } }
    public bool IsTechnologyStep => Step == 0;
    public bool IsSdkStep => Step == 1;
    public bool IsDetailsStep => Step == 2;
    public bool IsBusy { get => _busy; internal set { _busy = value; ChangedAll(); } }
    public bool CanRetrySdk => IsSdkStep && !IsBusy && Kind is not null;
    public string Technology => Kind == BuilderProjectKind.Node ? "Node.js · React / TypeScript" : ".NET · Avalonia";
    public string DisplayName
    {
        get => _name;
        set
        {
            _name = value ?? string.Empty;
            if (!_idOverridden) _packageId = paths.ToPackageId(_name);
            if (!_folderOverridden) _folder = paths.ToProjectName(_name);
            ChangedAll();
        }
    }
    public string PackageId { get => _packageId; set { _idOverridden = true; _packageId = value ?? string.Empty; ChangedAll(); } }
    public string ParentFolder { get => _parent; set { _parent = value ?? string.Empty; ChangedAll(); } }
    public string Subfolder { get => _folder; set { _folderOverridden = true; _folder = value ?? string.Empty; ChangedAll(); } }
    public string FullPath
    {
        get { try { return paths.ResolveCreationFolder(ParentFolder, Subfolder); } catch (Exception e) when (BuilderProjectApplicationService.IsRecoverable(e)) { return string.Empty; } }
    }
    public string ValidationMessage => string.IsNullOrWhiteSpace(DisplayName) ? "Enter a package name."
        : !Sunder.Sdk.Packaging.PackageId.TryParse(PackageId, out _) ? "Use a lowercase dot-separated package ID."
        : FullPath.Length == 0 ? "Choose an absolute parent folder and a single subfolder name." : string.Empty;
    public bool CanCreate => !IsBusy && IsDetailsStep && Installation is not null && ValidationMessage.Length == 0;
    public string Message => _message;
    public bool ShowMessage => !HasError && _message.Length > 0;
    public string ErrorMessage => _error;
    public bool HasError => _error.Length > 0;
    public string Output => _output;
    public bool HasOutput => _output.Length > 0;

    internal void Reset(string parent)
    {
        _idOverridden = _folderOverridden = false;
        _name = _packageId = _folder = _message = _error = _output = string.Empty;
        _parent = parent;
        Kind = null; Installation = null; _step = 0; _busy = false;
        ChangedAll();
    }
    internal void Apply(BuilderJobSnapshot snapshot)
    {
        _message = snapshot.Message;
        _output = BuilderProjectViewModel.BoundOutput(snapshot.Output);
        if (snapshot.Installation is { } installation) Installation = installation;
        _error = snapshot.State == "failed" ? snapshot.Error ?? snapshot.Message
            : snapshot.State == "cancelled" ? "Operation cancelled. You can retry with the same inputs." : string.Empty;
        ChangedAll();
    }
    internal void SetError(string error) { _error = error; ChangedAll(); }
}
