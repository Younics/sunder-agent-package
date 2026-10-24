using System.Runtime.ExceptionServices;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

/// <summary>One explicit development load and its status observer. It never builds or observes source changes.</summary>
[SunderPackageCodeRole(SunderPackageCodeRole.App)]
internal sealed class BuilderDevelopmentSession(
    BuilderProjectInspection project, BuilderProjectApplicationService application, IPackageDevelopmentService development, CancellationToken lifetime,
    Action<BuilderDevelopmentSession> changed)
{
    private IPackageDevelopmentSession? _handle;
    private CancellationTokenSource? _monitorCancellation;
    private Task _monitorTask = Task.CompletedTask;

    internal BuilderProjectInspection Project { get; } = project;
    internal string Identity { get; } = Guid.NewGuid().ToString("N");
    internal string SourcePackageId => Project.PackageId;
    internal string SourceEntryPoint => Project.EntryPoint;
    internal IPackageDevelopmentSession Handle => _handle ?? throw new InvalidOperationException("No development handle has been acquired.");
    internal bool HasHandle => _handle is not null;
    internal string? DevelopmentFolder { get; private set; }
    internal bool ReleaseStarted { get; private set; }
    internal bool Released { get; private set; }
    internal PackageDevelopmentStatus? Status { get; private set; }
    internal BuilderJobSnapshot? PendingHotreload { get; set; }
    internal PackageDevelopmentRevision? ReloadCommittedRevision { get; set; }

    internal async Task LoadAsync(string folder, CancellationToken token)
    {
        if (ReleaseStarted) throw new InvalidOperationException("Retry Unload before acquiring another development handle.");
        if (_handle is not null) throw new InvalidOperationException("This project already has a development owner. Use explicit Hotreload or Unload.");
        token.ThrowIfCancellationRequested();
        _handle = await development.LoadAsync(folder, watch: false, token).ConfigureAwait(false);
        if (_handle.PackageId != SourcePackageId)
            throw new InvalidDataException("The acquired development handle identifies a different package. Its exact owner must be released before retrying.");
        DevelopmentFolder = folder;
        _monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _monitorTask = Task.Run(MonitorAsync);
        changed(this);
    }

    private async Task MonitorAsync()
    {
        var token = _monitorCancellation!.Token;
        PackageDevelopmentStatus? last = null;
        try
        {
            while (true)
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    last = await Handle.GetStatusAsync(token).ConfigureAwait(false);
                    if (!token.IsCancellationRequested) { Status = last; changed(this); }
                }
                catch (OperationCanceledException error) when (error.CancellationToken == token && token.IsCancellationRequested) { throw; }
                catch (Exception error) when (error is not OperationCanceledException && BuilderProjectApplicationService.IsRecoverable(error))
                {
                    var unavailable = (last ?? new(Handle.PackageId, false, false, PackageDevelopmentRuntimeReadiness.Unknown, null, false))
                        with { IsLoaded = false, HostState = PackageDevelopmentHostState.Reconnecting,
                            RuntimeReadiness = PackageDevelopmentRuntimeReadiness.Unknown, AppReadiness = PackageDevelopmentAppReadiness.Unavailable,
                            RuntimeRevision = null, AppliedRevision = null, ErrorMessage = error.Message };
                    if (!token.IsCancellationRequested) { Status = unavailable; changed(this); }
                }
                await Task.Delay(TimeSpan.FromSeconds(1), token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (error.CancellationToken == token && token.IsCancellationRequested) { }
    }

    internal async Task ReleaseAsync()
    {
        if (Released) return;
        ReleaseStarted = true;
        List<Exception> errors = [];
        if (_monitorCancellation is not null) await CaptureAsync(errors, _monitorCancellation.CancelAsync).ConfigureAwait(false);
        await CaptureAsync(errors, () => _monitorTask).ConfigureAwait(false);
        changed(this);
        // A cancelled finite job may still own this project or SDK. Only its exact
        // execution/cleanup evidence permits withdrawing the last development owner.
        await CaptureAsync(errors, () => application.ReconcileProjectJobsAsync(Project.ProjectFolder)).ConfigureAwait(false);
        if (application.HasUnconfirmedJob(Project.ProjectFolder))
            throw new BuilderJobCleanupException(
                $"Builder job cleanup ({string.Join(", ", application.UnconfirmedProjectJobIds(Project.ProjectFolder))}) for '{SourcePackageId}' is unconfirmed; the development session remains owned. Reconnect and retry Unload.",
                new AggregateException("Builder finite-job cleanup could not be proven.", errors));
        await CaptureAsync(errors, async () =>
        {
            if (_handle is not null) await _handle.DisposeAsync().ConfigureAwait(false);
            _handle = null;
            Released = true;
            changed(this);
        }).ConfigureAwait(false);
        if (Released) _monitorCancellation?.Dispose();
        if (errors.Count == 1) ExceptionDispatchInfo.Capture(errors[0]).Throw();
        if (errors.Count > 1) throw new AggregateException("Builder development session cleanup failed.", errors);
    }

    private static async Task CaptureAsync(List<Exception> errors, Func<Task> operation)
    {
        try { await operation().ConfigureAwait(false); }
        catch (Exception error) { errors.Add(error); }
    }
}
