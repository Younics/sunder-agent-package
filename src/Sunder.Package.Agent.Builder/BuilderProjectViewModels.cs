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
    private string? _errorOperation;
    private string _watchError = string.Empty;
    private string _sourceError = string.Empty;
    private string _output = string.Empty;
    private bool _busy;
    private bool _missing;
    private bool _conflict;
    private bool _hasSession;
    private bool _releasePending;
    private bool _watchRunning;
    private bool _watchWaiting;
    private BuilderJobConnectionState _watchConnection;
    private PackageDevelopmentStatus? _developmentStatus;
    private BuilderJobPhase _phase;
    private string _requestedOperation = "operation";
    private string _snapshotState = string.Empty;
    private DateTimeOffset? _lastSourceChangeAtUtc;
    private DateTimeOffset? _lastSuccessfulBuildAtUtc;
    private PackageDevelopmentRevision? _lastAppliedRevision;
    private DateTimeOffset? _lastAppliedObservedAtUtc;
    private bool _outputExpanded;

    public BuilderProjectViewModel(BuilderProjectRecord record)
    {
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
    public bool IsLoaded => _developmentStatus?.IsLoaded == true;
    public bool IsBusy { get => _busy; internal set { if (Set(ref _busy, value)) ChangedAll(); } }
    public bool Watch { get => _record.Watch; internal set { _record = _record with { Watch = value }; ChangedAll(); } }
    public bool WatchRunning => _watchRunning;
    public bool WatchWaitingForChanges => _watchWaiting;
    public bool WatchReconnecting => _watchConnection == BuilderJobConnectionState.Reconnecting;
    public bool CanOperate => !IsBusy && !IsMissing && Inspection is not null;
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
    public string ErrorMessage => string.Join(Environment.NewLine, new[] { _sourceError, _error, _watchError }.Where(message => message.Length > 0));
    public bool HasError => ErrorMessage.Length > 0;
    public string Output => _output;
    public bool HasOutput => _output.Length > 0;
    public bool IsOutputExpanded { get => _outputExpanded; set => Set(ref _outputExpanded, value); }
    public string? ArchivePath { get; private set; }
    public bool HasArchive => !string.IsNullOrEmpty(ArchivePath);
    public string LoadStateText => _releasePending ? "Release unconfirmed" : HasSession ? (IsLoaded ? "Loaded" : "Connecting…") : "Unloaded";
    public string StateText => IsBusy ? _requestedOperation switch
        {
            "load" => "Loading…", "unload" => "Unloading…", "build" => "Building…", "archive" => "Creating archive…",
            "save-metadata" => "Saving…", "setup" => "Setting up…", _ => "Updating…",
        }
        : IsMissing ? "Folder missing" : HasSession ? LoadStateText
        : HasError ? "Needs attention" : SetupIncomplete ? "Setup required" : "Unloaded";
    public bool IsBuildRunning => (IsBusy || WatchRunning) && _snapshotState == "running"
        && _phase is BuilderJobPhase.Restoring or BuilderJobPhase.Building or BuilderJobPhase.Validating or BuilderJobPhase.Packaging;
    public string BuildActionText => HasSession ? "Build + reload" : "Build";
    public string WatchSummaryText => HasSession && _watchConnection == BuilderJobConnectionState.StopUnconfirmed ? "Stop unconfirmed"
        : !Watch ? "Off" : !HasSession ? "Starts after Load"
        : WatchReconnecting ? "Reconnecting" : WatchWaitingForChanges || _phase == BuilderJobPhase.BuildFailed ? "Waiting for an edit"
        : WatchRunning ? "On" : "Stopped";
    public string ActivityText => IsBuildRunning ? _phase switch
        {
            BuilderJobPhase.Restoring => "Restoring dependencies…", BuilderJobPhase.Validating => "Validating package output…",
            BuilderJobPhase.Packaging => "Checking contracts and creating archive…", _ => "Building package…",
        }
        : IsBusy ? StateText
        : _releasePending ? "Owner release is unconfirmed. Reconnect and retry Unload."
        : HasSession && !IsLoaded ? "Waiting for the Runtime…"
        : HasSession && _developmentStatus?.AppReadiness == PackageDevelopmentAppReadiness.Pending ? "Opening package view…"
        : HasSession && WatchReconnecting ? "Reconnecting; the last loaded package is retained."
        : HasSession && _phase == BuilderJobPhase.BuildFailed ? "Build failed. Fix the source and save to retry; the last good package is retained."
        : HasSession && WatchRunning ? "Watching for source changes"
        : HasSession && WatchWaitingForChanges ? "Fix the source and save to retry. The last good package is retained."
        : HasSession && Watch ? "Automatic rebuild is stopped" : HasSession ? "Automatic rebuild is off"
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
        : _developmentStatus?.RuntimeRevision is null ? "This Host has not supplied an applied-output revision."
        : _developmentStatus.AppliedRevision is null ? "Current Runtime output is not yet confirmed applied."
        : "Runtime and App activation match this output revision.";
    public bool HasDisplayNameEditReason => DisplayNameEditReason.Length > 0;
    public bool HasPackageIdEditReason => PackageIdEditReason.Length > 0;
    public bool ShowWatchNotice => HasSession && (_watchConnection == BuilderJobConnectionState.StopUnconfirmed
        || _developmentStatus is { HostState: PackageDevelopmentHostState.Ready, WatchEnabled: true, RequestedWatch: false });
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
    public string WatchStatusText => !HasSession ? "Watch builds source and reloads the development package after Load."
        : _releasePending ? "Development owner release is unconfirmed; retained ownership is not current loaded-state evidence. Retry Unload."
        : WatchReconnecting ? "Reconnecting to the same Builder job; source ownership is retained."
        : _watchConnection == BuilderJobConnectionState.StopUnconfirmed ? "Source watch shutdown is unconfirmed; project ownership is retained. Retry Unload after reconnecting."
        : WatchWaitingForChanges ? "Waiting for a source edit before restarting builds. The last good package remains loaded."
        : _developmentStatus is { HostState: PackageDevelopmentHostState.Ready, WatchEnabled: true, RequestedWatch: false }
            ? "Your watch is stopped. Output watching is still enabled by another owner."
            : WatchRunning ? "Source builds and package reload are being watched."
            : "Source watch is stopped; the loaded package is retained.";

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
        if (snapshot.Operation == "watch" && snapshot.Phase == BuilderJobPhase.BuildFailed && _phase != BuilderJobPhase.BuildFailed)
        {
            _watchError = "Automatic build failed. See build output; the last good package is retained.";
            IsOutputExpanded = true;
        }
        _phase = snapshot.Phase;
        _snapshotState = snapshot.State;
        if (snapshot.LastSourceChangeAtUtc is { } changed) ObserveSourceChange(changed);
        if (snapshot.LastSuccessfulBuildAtUtc is { } built && (_lastSuccessfulBuildAtUtc is null || built > _lastSuccessfulBuildAtUtc))
        {
            _lastSuccessfulBuildAtUtc = built;
            if (snapshot.Operation == "watch") ObserveSuccessfulWatchBuild();
        }
        if (snapshot.Project is { } project) ApplyInspection(project, acceptEdits);
        if (snapshot.Installation is { } installation) _record = _record with { Installation = installation };
        if (snapshot.ArchivePath is { } archive) ArchivePath = archive;
        _statusText = snapshot.ConnectionState == BuilderJobConnectionState.Reconnecting
            ? "Reconnecting to the same Builder job…" : snapshot.Message;
        if (snapshot.Operation == "watch") _watchConnection = snapshot.ConnectionState;
        _output = BoundOutput(snapshot.Output);
        if (snapshot.State == "failed") IsOutputExpanded = true;
        if (snapshot.State == "failed" && snapshot.Operation == "watch") _watchError = "Source watch stopped: " + (snapshot.Error ?? snapshot.Message);
        else if (snapshot.State == "failed") { _error = snapshot.Error ?? snapshot.Message; _errorOperation = snapshot.Operation; }
        else if (snapshot.State == "succeeded") { _error = string.Empty; _errorOperation = null; }
        if (snapshot.Operation is "create" or "setup" && snapshot.State != "running")
            _record = _record with { SetupIncomplete = snapshot.State != "succeeded" };
        // Polling can miss BuildReady. A live Watching snapshot carries the same
        // validated-build evidence, independently of Runtime/App application.
        if (snapshot.DevelopmentFolder is not null && (snapshot.State == "succeeded"
            || snapshot.State == "running" && snapshot.LastSuccessfulBuildAtUtc is not null
                && snapshot.Phase is BuilderJobPhase.BuildReady or BuilderJobPhase.Watching))
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
        _statusText = _output = _error = _watchError = string.Empty;
        _errorOperation = null;
        IsOutputExpanded = false;
    }
    internal void ObserveSourceChange(DateTimeOffset value)
    {
        if (_lastSourceChangeAtUtc is { } previous && value <= previous) return;
        _lastSourceChangeAtUtc = value;
        ChangedAll();
    }
    internal void SetError(string message) { _error = message; _errorOperation = null; ChangedAll(); }
    internal void SetError(Exception error)
    {
        _output = BuilderJobText.Safe(_output + $"\n[{_requestedOperation}] App operation failed:\n" + error + "\n", MaximumOutputCharacters);
        IsOutputExpanded = true;
        SetError(BuilderJobText.Safe(error.Message, 8192));
    }
    internal void SetWatchError(string message) { _watchError = message; ChangedAll(); }
    internal void SetWatchWaitingForChanges(bool value) { _watchWaiting = value; ChangedAll(); }
    internal void ObserveSuccessfulWatchBuild()
    {
        _watchError = string.Empty;
        if (_errorOperation is "build" or "setup" or "watch") { _error = string.Empty; _errorOperation = null; }
        ChangedAll();
    }
    internal void SetStatus(string message) { _statusText = message; ChangedAll(); }
    internal void SetSession(bool hasSession)
    {
        _hasSession = hasSession;
        if (!hasSession) { _developmentStatus = null; _releasePending = false; }
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
    internal void SetWatchRunning(bool value)
    {
        _watchRunning = value;
        if (value || _watchConnection == BuilderJobConnectionState.Reconnecting) _watchConnection = BuilderJobConnectionState.Connected;
        ChangedAll();
    }
    internal void SetWatchConnectionState(BuilderJobConnectionState value) { _watchConnection = value; ChangedAll(); }
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
