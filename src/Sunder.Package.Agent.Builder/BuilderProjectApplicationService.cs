using System.Diagnostics;
using System.Runtime.ExceptionServices;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

/// <summary>The App's typed boundary to the local Builder worker. Discovery never installs or builds.</summary>
public interface IBuilderBackend
{
    Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default);
    Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default);
    Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default);
    Task<IBuilderJobExecution> CaptureExecutionAsync(CancellationToken cancellationToken = default)
        => Task.FromException<IBuilderJobExecution>(new NotSupportedException("Builder jobs require exact worker execution capture before Start."));
    Task<PackageWorkerExecutionObservation> ObserveExecutionAsync(PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
        => Task.FromException<PackageWorkerExecutionObservation>(new NotSupportedException("Builder execution observations are unavailable."));
    Task<BuilderRecordedJobRecovery?> RecoverRecordedJobAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default);
}

public sealed class BuilderJobCleanupException(string message, Exception innerException) : IOException(message, innerException);

/// <summary>Journal-admitted finite job gateway. Accepted execution and uncertain-start evidence outlive presentation.</summary>
public sealed class BuilderProjectApplicationService(
    IBuilderBackend backend,
    IBuilderProjectStore projectStore,
    BuilderOperationQueue operations,
    IBuilderJobJournal journal) : IAsyncDisposable
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, Task> _active = new(StringComparer.Ordinal);
    private readonly Dictionary<string, OwnedJob> _uncertain = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BuilderJobStopResult> _completed = new(StringComparer.Ordinal);
    private readonly Queue<string> _completedOrder = new();
    private static readonly TimeSpan ReconciliationBudget = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);
    private Task? _disposal;

    public Task<IReadOnlyList<BuilderProjectRecord>> LoadProjectsAsync(CancellationToken cancellationToken = default)
        => projectStore.LoadAsync(cancellationToken);

    public Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default)
        => backend.DiscoverAsync(folder, cancellationToken);

    public Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default)
        => backend.InspectAsync(entryPoint, cancellationToken);

    public Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default)
        => backend.GetToolchainsAsync(kind, checkUpdates, cancellationToken);

    /// <summary>Reconciles an uncertain job stop after transport recovery, using bounded independent cleanup.</summary>
    public Task<BuilderJobStopResult> StopJobAsync(string jobId) => StopJobAsync(jobId, null);

    private async Task<BuilderJobStopResult> StopJobAsync(string jobId, BuilderJobScope? scope)
    {
        OwnedJob? owned;
        lock (_sync)
        {
            if (_completed.TryGetValue(jobId, out var terminal)) return terminal;
            _uncertain.TryGetValue(jobId, out owned);
        }
        if (owned is null)
            return RememberStop(await journal.ReconcileAsync(jobId, backend, scope: scope).ConfigureAwait(false));
        if (!owned.StartAttempted || owned.Admission.HasPositiveNoStartProof)
        {
            using var noStart = new CancellationTokenSource(ReconciliationBudget);
            return RememberStop(await journal.AbortKnownNotStartedAsync(owned.Admission, noStart.Token, scope).ConfigureAwait(false));
        }
        if (owned.Admission.TerminalReceipt is { } receipt)
            return await CommitStopAsync(owned, receipt.Result, scope: scope).ConfigureAwait(false);
        var started = Stopwatch.GetTimestamp();
        using (var readback = new CancellationTokenSource(ReconciliationBudget))
        {
            try
            {
                if (await journal.ReadCompletedAsync(owned.Reservation, readback.Token, scope).ConfigureAwait(false) is { } completed)
                    return RememberStop(completed); // Completion may have committed before its acknowledgement was lost.
            }
            catch (OperationCanceledException error) when (readback.IsCancellationRequested)
            { throw new TimeoutException("Builder journal readback did not reconnect within the 12-second reconciliation budget.", error); }
        }
        var cleanup = await CancelAndReadAsync(owned, ReconciliationBudget - Stopwatch.GetElapsedTime(started)).ConfigureAwait(false);
        if (cleanup.Result is null)
        {
            if (cleanup.Failure is { } error && IsBoundaryFailure(error)) ExceptionDispatchInfo.Capture(error).Throw();
            throw new IOException("The Builder job has not confirmed shutdown. Reconnect before changing this project or SDK.");
        }
        return await CommitStopAsync(owned, cleanup.Result, cleanup.Remaining, scope).ConfigureAwait(false);
    }

    public async Task<BuilderJobSnapshot> RunJobAsync(BuilderJobRequest request,
        Func<BuilderJobSnapshot, Task>? changed = null, CancellationToken cancellationToken = default, string? projectId = null)
    {
        // Allocate once, before either the queue or an uncertain RPC. Application
        // runs never reuse an already owned ID, including across backend changes.
        request = request with { Id = request.Id ?? Guid.NewGuid().ToString("N") };
        var run = new JobRun(request);
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposal is not null, this);
            if (_active.ContainsKey(request.Id) || _uncertain.ContainsKey(request.Id) || _completed.ContainsKey(request.Id))
                throw new InvalidOperationException("This Builder job identity is already owned. Reconcile it rather than starting it again.");
            _active.Add(request.Id, completed.Task);
        }
        try
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
            try
            {
                return await journal.RunOwnedAsync(async scope =>
                {
                    run.Scope = scope;
                    var context = scope.Context;
                    using var job = CancellationTokenSource.CreateLinkedTokenSource(linked.Token,
                        context.SettlementRequested, context.CancellationToken);
                    try
                    {
                        return await EnqueueFiniteJobAsync(run, projectId, changed, job.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException error) when (error.CancellationToken == job.Token && job.IsCancellationRequested)
                    {
                        if (linked.IsCancellationRequested)
                            throw new OperationCanceledException(error.Message, error, linked.Token);
                        if (context.CancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(error.Message, error, context.CancellationToken);
                        // Settlement before Start is a positive no-dispatch case,
                        // not an invented terminal outcome for a dispatched job.
                        if (!context.SettlementRequested.IsCancellationRequested || run.AttemptedStart) throw;
                        if (run.Admission is { PendingWriteAttempted: true } admission)
                            run.StopResult = RememberStop(await journal.AbortKnownNotStartedAsync(admission, scope: scope).ConfigureAwait(false));
                        return new BuilderJobSnapshot
                        {
                            Id = request.Id!, Operation = request.Operation, State = "cancelled",
                            Message = "App settlement stopped the job before dispatch.", Output = string.Empty,
                        };
                    }
                }, linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested
                && error.CancellationToken == linked.Token && !PresentationExceptionPolicy.IsFatal(error))
            {
                // Translate only the private token we introduced. Presentation must not have to
                // treat every cancellation racing its owner stop as that owner's cancellation.
                throw new OperationCanceledException(error.Message, error, cancellationToken);
            }
        }
        finally
        {
            lock (_sync)
            {
                // Record positive no-attempt authority separately from any job result.
                // Even an unexpected/fatal exit after dispatch retains its exact capture.
                if (run.StopResult is null && run.Owned is { } owned && run.Admission?.PendingWriteAttempted == true)
                    _uncertain[request.Id] = owned;
                else if (!run.AttemptedStart && run.StopResult is null)
                    RememberStop(BuilderJobStopResult.NotStarted(request.Id));
                _active.Remove(request.Id);
            }
            completed.TrySetResult();
        }
    }

    private async Task<BuilderJobSnapshot> EnqueueFiniteJobAsync(JobRun run, string? projectId,
        Func<BuilderJobSnapshot, Task>? changed, CancellationToken token)
    {
        var request = run.Request;
        var sdk = request.Operation.EndsWith("-sdk", StringComparison.Ordinal);
        var technology = request.Kind == BuilderProjectKind.Dotnet ? ".NET" : "Node.js";
        var action = request.Operation switch
        {
            "ensure-sdk" => "Set up SDK", "repair-sdk" => "Repair SDK", "remove-sdk" => "Remove SDK",
            "create" => "Create project", "setup" => "Set up project", "build" => "Build",
            "archive" => "Create package archive", "save-metadata" => "Save source metadata", _ => request.Operation,
        };
        var title = $"{action} · {(sdk ? technology : request.DisplayName ?? Path.GetFileName(request.ProjectFolder) ?? technology)}";
        Dictionary<string, string> metadata = new(StringComparer.Ordinal)
        {
            ["builder.operation"] = request.Operation, ["builder.jobId"] = request.Id!,
            ["builder.kind"] = request.Kind == BuilderProjectKind.Dotnet ? "dotnet" : "node",
        };
        if ((projectId ?? request.PackageId ?? request.EntryPoint) is { } identity) metadata["builder.projectId"] = identity;
        if (request.PackageId is { } packageId) metadata["builder.packageId"] = packageId;
        if (request.ProjectFolder is { } folder) metadata["builder.projectFolder"] = folder;
        if (request.EntryPoint is { } entry) metadata["builder.entryPoint"] = entry;
        try
        {
            return await operations.EnqueueAsync(title, sdk ? BackgroundTaskIndicator.Settings : BackgroundTaskIndicator.Main,
                metadata, context => ReconcileAndRunAsync(run, async snapshot =>
                {
                    if (snapshot.State == "running") context.ReportIndeterminate(snapshot.Message);
                    else context.ReportStatus(snapshot.Error ?? snapshot.Message);
                    if (changed is not null) await changed(snapshot).ConfigureAwait(false);
                }, context.CancellationToken), token).ConfigureAwait(false);
        }
        catch (BuilderQueueCanceledException error)
        {
            // A shell-owned cancellation has no caller token, but it is a real terminal outcome.
            // No backend ran, so retain only the request identity, never project/session results.
            var snapshot = new BuilderJobSnapshot
            {
                Id = request.Id!, Operation = request.Operation, State = "cancelled", Message = error.Message, Output = string.Empty,
            };
            if (changed is not null) await changed(snapshot).ConfigureAwait(false);
            return snapshot;
        }
    }

    private async Task<BuilderJobSnapshot> ReconcileAndRunAsync(JobRun run,
        Func<BuilderJobSnapshot, Task>? changed, CancellationToken token)
    {
        OwnedJob[] uncertain;
        lock (_sync) uncertain = _uncertain.Values.ToArray();
        foreach (var prior in uncertain.Where(prior => OwnershipOverlaps(prior.Request, run.Request)))
        {
            token.ThrowIfCancellationRequested();
            await StopJobAsync(prior.Request.Id!, run.Scope).ConfigureAwait(false);
        }
        return await PollJobAsync(run, changed, token).ConfigureAwait(false);
    }

    private async Task<BuilderJobSnapshot> PollJobAsync(JobRun run,
        Func<BuilderJobSnapshot, Task>? changed, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var admission = run.Admission = new BuilderJobAdmission(run.Request);
        OwnedJob? owned = null;
        try
        {
            try
            {
                await journal.AdmitAndStartAsync(admission, backend,
                    current => ReadWithCallerHandoffAsync(backend.CaptureExecutionAsync, null, current), token, run.Scope).ConfigureAwait(false);
            }
            catch (Exception error) when (admission.StartAttempted && admission.StopResult is null
                && !token.IsCancellationRequested && error is not InvalidDataException
                && (IsReconnecting(error) || error is IOException or HttpRequestException or TimeoutException))
            {
                // Acceptance may have happened. Only read this job on this backend;
                // never replay Start against a newly selected Worker incarnation.
            }
            finally
            {
                run.AttemptedStart = admission.StartAttempted;
                if (admission.Execution is { } execution && admission.Reservation is { } reservation)
                    owned = run.Owned = new OwnedJob(admission.Request, execution, reservation, admission);
                if (admission.StopResult is { } stopped) run.StopResult = RememberStop(stopped);
            }

            while (true)
            {
                var snapshot = await ReadJobAsync(owned!, changed, token, run.Scope).ConfigureAwait(false);
                if (changed is not null) await changed(snapshot).ConfigureAwait(false);
                if (snapshot.State != "running")
                { run.StopResult = await CommitStopAsync(owned!, BuilderJobStopResult.KnownTerminal(snapshot), scope: run.Scope).ConfigureAwait(false); return snapshot; }
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }
        catch (Exception error) when (admission.StartAttempted && !admission.HasPositiveNoStartProof && owned is not null && !PresentationExceptionPolicy.IsFatal(error)
            && (!IsRecoverable(error)
                || error is SunderRpcException || IsBoundaryFailure(error)
                || error is OperationCanceledException && !PresentationCancellation.IsExpected(error, token)))
        {
            // Cleanup must not need another presentation callback, and a foreign cancellation or
            // callback fault must not be replaced by a successful-looking terminal job snapshot.
            var cleanup = await CancelAndReadAsync(owned).ConfigureAwait(false);
            if (cleanup.Result is null)
            {
                var unconfirmed = await UnconfirmedAsync(owned,
                    IsRecoverable(error) && error is not OperationCanceledException ? changed : null, error, cleanup.Remaining, run.Scope).ConfigureAwait(false);
                // Retain exact ownership without disguising a permission/protocol rejection as a reconnect.
                if (IsBoundaryFailure(error)) throw;
                throw unconfirmed;
            }
            run.StopResult = await CommitStopAsync(owned, cleanup.Result, cleanup.Remaining, run.Scope).ConfigureAwait(false);
            throw;
        }
        catch (Exception error) when (admission.StartAttempted && !admission.HasPositiveNoStartProof && owned is not null && IsRecoverable(error))
        {
            // A canceled/failed start may already have created source. Never use the canceled caller token
            // for cleanup and always fetch the final partial result before relinquishing this job.
            var cleanup = await CancelAndReadAsync(owned).ConfigureAwait(false);
            if (cleanup.Result is { } stopped)
            {
                stopped = run.StopResult = await CommitStopAsync(owned, stopped, cleanup.Remaining, run.Scope).ConfigureAwait(false);
                if (stopped.TerminalSnapshot is { } final)
                {
                    if (changed is not null) await changed(final).ConfigureAwait(false);
                    return final;
                }
                // Host cleanup permits resource/exclusion release, not a fabricated business outcome.
                throw new BuilderJobOutcomeUnknownException(stopped, error);
            }
            if (cleanup.Failure is { } failure && IsBoundaryFailure(failure)) ExceptionDispatchInfo.Capture(failure).Throw();
            throw await UnconfirmedAsync(owned, changed, error, cleanup.Remaining, run.Scope).ConfigureAwait(false);
        }
    }

    private async Task<BuilderJobSnapshot> ReadJobAsync(OwnedJob owned,
        Func<BuilderJobSnapshot, Task>? changed, CancellationToken token, BuilderJobScope? scope)
    {
        var snapshot = await ReadWithCallerHandoffAsync(
            current => owned.Execution.GetJobAsync(owned.Request.Id!, current),
            owned.Last is { } last && changed is not null
                ? () => changed(last with { ConnectionState = BuilderJobConnectionState.Reconnecting }) : null, token).ConfigureAwait(false);
        snapshot = AcceptSnapshot(owned, snapshot);
        await journal.SavePartialAsync(owned.Reservation, snapshot, token, scope).ConfigureAwait(false);
        return snapshot;
    }

    private static BuilderJobSnapshot AcceptSnapshot(OwnedJob owned, BuilderJobSnapshot snapshot)
    {
        var request = owned.Request;
        if (snapshot.Id != request.Id || snapshot.Operation != request.Operation || snapshot.BackendId != request.BackendId
            || snapshot.State is not ("running" or "succeeded" or "failed" or "cancelled"))
            throw new InvalidDataException("Builder returned a job from a different backend, request or operation, or an invalid outcome.");
        return owned.Last = snapshot;
    }

    // Only reads use this loop. Start is
    // deliberately absent: a reconnect cannot justify replaying a mutation.
    private static async Task<T> ReadWithCallerHandoffAsync<T>(Func<CancellationToken, Task<T>> read,
        Func<Task>? reconnecting, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(ReconciliationBudget);
        var started = Stopwatch.GetTimestamp();
        var notified = false;
        while (true)
        {
            try
            {
                var remaining = ReconciliationBudget - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) throw new TimeoutException("Builder job observation could not reconnect within its bounded budget.");
                return await read(deadline.Token).WaitAsync(remaining, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == deadline.Token)
            { throw new OperationCanceledException(error.Message, error, token); }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && error.CancellationToken == deadline.Token)
            { throw new TimeoutException("Builder job observation could not reconnect within its bounded budget.", error); }
            catch (Exception error) when (IsReconnecting(error) && !deadline.IsCancellationRequested)
            {
                if (!notified && reconnecting is not null) await reconnecting().ConfigureAwait(false);
                notified = true;
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
        }
    }

    private async Task<CleanupAttempt> CancelAndReadAsync(OwnedJob owned, TimeSpan? remaining = null)
    {
        var budget = remaining ?? ReconciliationBudget;
        if (budget <= TimeSpan.Zero) return new(null, new TimeoutException("Builder cleanup exhausted its reconciliation budget."));
        var started = Stopwatch.GetTimestamp();
        var result = await CancelAndReadCoreAsync(owned, budget).ConfigureAwait(false);
        return result with { Remaining = budget - Stopwatch.GetElapsedTime(started) };
    }

    private async Task<CleanupAttempt> CancelAndReadCoreAsync(OwnedJob owned, TimeSpan budget)
    {
        using var cleanup = new CancellationTokenSource(budget);
        if (owned.Last is { State: "succeeded" or "failed" or "cancelled" } terminal)
            return new(BuilderJobStopResult.KnownTerminal(terminal));
        try
        {
            try
            {
                // Each attempt remains fenced to this capture. A failed Cancel is not
                // stop authority; read the old job or its independently owned Host receipt.
                await owned.Execution.CancelJobAsync(owned.Request.Id!, cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (IsRecoverable(error) && !cleanup.IsCancellationRequested)
            {
                if (IsBoundaryFailure(error)) return new(null, error);
            }
            do
            {
                Exception? readFailure = null;
                try
                {
                    var snapshot = AcceptSnapshot(owned, await owned.Execution.GetJobAsync(owned.Request.Id!, cleanup.Token)
                        .WaitAsync(cleanup.Token).ConfigureAwait(false));
                    if (snapshot.State != "running") return new(BuilderJobStopResult.KnownTerminal(snapshot));
                }
                catch (Exception error) when (IsRecoverable(error) && !cleanup.IsCancellationRequested)
                {
                    if (IsBoundaryFailure(error)) return new(null, error);
                    readFailure = error;
                }

                PackageWorkerExecutionObservation? observation = null;
                try
                {
                    observation = await owned.Execution.ObserveCleanupAsync(cleanup.Token).WaitAsync(cleanup.Token).ConfigureAwait(false);
                    if (observation.Reference != owned.Execution.Reference || !Enum.IsDefined(observation.State))
                        throw new InvalidDataException("Builder received cleanup evidence for a different execution or an invalid state.");
                    if (observation.State == PackageWorkerExecutionState.CleanupConfirmed)
                        return new(BuilderJobStopResult.ExecutionCleaned(owned.Request.Id!, owned.Last, observation));
                }
                catch (Exception error) when (IsRecoverable(error) && !cleanup.IsCancellationRequested)
                {
                    if (IsBoundaryFailure(error) || !IsReconnecting(error)) return new(null, error);
                }

                // Ordinary disappearance/NotFound is never confirmation. Keep the
                // exact capture and partial result; a later explicit reconciliation
                // may observe the Host's positive, historical cleanup receipt.
                if (readFailure is not null && !IsReconnecting(readFailure)
                    && observation?.State != PackageWorkerExecutionState.CleanupPending)
                    return new(null, readFailure);
                await Task.Delay(100, cleanup.Token).ConfigureAwait(false);
            } while (true);
        }
        catch (Exception error) when (IsRecoverable(error))
        {
            // Preserve partial source even when a disconnected worker cannot confirm shutdown.
            return new(null, error);
        }
    }

    private async Task<BuilderJobCleanupException> UnconfirmedAsync(OwnedJob owned,
        Func<BuilderJobSnapshot, Task>? changed, Exception error, TimeSpan remaining, BuilderJobScope? scope)
    {
        const string message = "Builder could not confirm that the job stopped. Reconnect before retrying.";
        lock (_sync) _uncertain[owned.Request.Id!] = owned;
        if (remaining > TimeSpan.Zero)
        {
            using var persistence = new CancellationTokenSource(remaining);
            try
            {
                await journal.SaveFailureAsync(owned.Reservation, error.Message, persistence.Token, scope).ConfigureAwait(false);
                if (owned.Last is { } partial) await journal.SavePartialAsync(owned.Reservation, partial, persistence.Token, scope).ConfigureAwait(false);
            }
            catch (Exception persistenceError) when (IsRecoverable(persistenceError))
            { error = new AggregateException("Builder stop and ownership persistence did not both confirm completion.", error, persistenceError); }
        }
        if (owned.Last is { } last && changed is not null)
            await changed(last with { ConnectionState = BuilderJobConnectionState.StopUnconfirmed }).ConfigureAwait(false);
        return new(message + " " + error.Message, error);
    }

    private BuilderJobStopResult RememberStop(BuilderJobStopResult result)
    {
        lock (_sync)
        {
            _uncertain.Remove(result.JobId);
            if (_completed.TryAdd(result.JobId, result)) _completedOrder.Enqueue(result.JobId);
            while (_completedOrder.Count > 64) _completed.Remove(_completedOrder.Dequeue());
            return _completed.GetValueOrDefault(result.JobId) ?? result;
        }
    }

    private async Task<BuilderJobStopResult> CommitStopAsync(OwnedJob owned, BuilderJobStopResult result, TimeSpan? remaining = null,
        BuilderJobScope? scope = null)
    {
        var budget = remaining ?? ReconciliationBudget;
        if (budget <= TimeSpan.Zero) throw new TimeoutException("Builder stop proof arrived without remaining reconciliation time to acknowledge its journal update. Retry reconciliation; do not replay Start.");
        using var persistence = new CancellationTokenSource(budget);
        try { result = await journal.CompleteOwnedAsync(owned.Admission, result, persistence.Token, scope).ConfigureAwait(false); }
        catch (OperationCanceledException error) when (persistence.IsCancellationRequested)
        { throw new TimeoutException("Builder stop proof could not acknowledge its journal update within the remaining reconciliation budget.", error); }
        return RememberStop(result);
    }

    internal bool IsJobStopConfirmed(string? jobId)
    {
        lock (_sync) return jobId is not null && _completed.ContainsKey(jobId);
    }

    private static bool IsBoundaryFailure(Exception error)
        => error is InvalidDataException or UnauthorizedAccessException
            or SunderRpcException { Error.Kind: SunderRpcErrorKind.PermissionDenied or SunderRpcErrorKind.Protocol or SunderRpcErrorKind.Validation }
            || error is AggregateException aggregate && aggregate.InnerExceptions.Any(IsBoundaryFailure)
            || error.InnerException is { } inner && IsBoundaryFailure(inner);

    private static bool IsReconnecting(Exception error)
        => error is SunderRpcException rpc
            && (((rpc.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.ResourceExhausted)
                    && rpc.Error.Code != "rpc.worker-execution.unsupported")
                || rpc.Error is { Kind: SunderRpcErrorKind.StaleEndpoint, Code: "rpc.caller-retired" });

    internal static bool IsRecoverable(Exception error)
        => error is not PresentationCallbackException && !PresentationExceptionPolicy.IsFatal(error)
            && (error is not AggregateException aggregate || aggregate.InnerExceptions.All(IsRecoverable))
            && (error.InnerException is null || IsRecoverable(error.InnerException));

    internal bool HasUnconfirmedJob(string projectFolder)
        => UnconfirmedProjectJobs(projectFolder).Length != 0;

    internal IReadOnlyList<string> UnconfirmedProjectJobIds(string projectFolder)
        => UnconfirmedProjectJobs(projectFolder).Select(job => job.Request.Id!).ToArray();

    private OwnedJob[] UnconfirmedProjectJobs(string projectFolder)
    {
        OwnedJob[] uncertain;
        lock (_sync) uncertain = _uncertain.Values.ToArray();
        var scope = new BuilderJobExclusionScope(Path.TrimEndingDirectorySeparator(new BuilderPathService().CanonicalEntryPoint(projectFolder)), false);
        return uncertain.Where(owned => owned.Reservation.Scope.Overlaps(scope)).ToArray();
    }

    internal async Task ReconcileProjectJobsAsync(string projectFolder)
    {
        var uncertain = UnconfirmedProjectJobs(projectFolder);
        foreach (var job in uncertain) await StopJobAsync(job.Request.Id!).ConfigureAwait(false);
    }

    private static bool OwnershipOverlaps(BuilderJobRequest left, BuilderJobRequest right)
        => BuilderJobExclusionScope.Create(left).Overlaps(BuilderJobExclusionScope.Create(right));

    private sealed class OwnedJob(BuilderJobRequest request, IBuilderJobExecution execution,
        BuilderJobReservation reservation, BuilderJobAdmission admission)
    {
        public BuilderJobRequest Request { get; } = request;
        public IBuilderJobExecution Execution { get; } = execution;
        public BuilderJobReservation Reservation { get; } = reservation;
        public BuilderJobAdmission Admission { get; } = admission;
        public bool StartAttempted => Admission.StartAttempted;
        public BuilderJobSnapshot? Last { get; set; }
    }

    private sealed class JobRun(BuilderJobRequest request)
    {
        public BuilderJobRequest Request { get; } = request;
        public BuilderJobScope? Scope { get; set; }
        public BuilderJobAdmission? Admission { get; set; }
        public OwnedJob? Owned { get; set; }
        public bool AttemptedStart { get; set; }
        public BuilderJobStopResult? StopResult { get; set; }
    }

    private sealed record CleanupAttempt(BuilderJobStopResult? Result, Exception? Failure = null)
    { public TimeSpan Remaining { get; init; } }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new(_disposal ??= Task.Run(async () =>
        {
            await _lifetime.CancelAsync().ConfigureAwait(false);
            Task[] active;
            lock (_sync) active = _active.Values.ToArray();
            await Task.WhenAll(active).ConfigureAwait(false);
            string[] uncertain;
            lock (_sync) uncertain = _uncertain.Keys.ToArray();
            try { await Task.WhenAll(uncertain.Select(StopJobAsync)).ConfigureAwait(false); }
            finally { _lifetime.Dispose(); }
        }));
    }
}
