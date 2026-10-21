using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Packaging;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Builder;

/// <summary>Mandatory, lazy App-workspace admission journal. Bookmarks carry no execution ownership.</summary>
public sealed class BuilderJobJournal : IBuilderJobJournal
{
    internal const int MaximumPending = 256;
    internal const int MaximumDocumentBytes = 1024 * 1024;
    private const int MaximumHistory = 64;
    private const string UpdateKey = "builder.admission.v1";
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(12);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 32,
    };
    private readonly IBuilderJobJournalStorage _storage;
    private readonly IPackageAppStateUpdates _updates;

    [ActivatorUtilitiesConstructor]
    public BuilderJobJournal(IPackageContext packageContext, IPackageAppStateUpdates updates)
        : this(new BuilderJobJournalFiles(packageContext), updates) { }

    // Tests explicitly supply a storage adapter. Production has no null/optional/in-memory fallback.
    internal BuilderJobJournal(IBuilderJobJournalStorage storage, IPackageAppStateUpdates updates)
    { _storage = storage; _updates = updates; }

    public async Task<string> AdmitAndStartAsync(BuilderJobAdmission admission, IBuilderBackend backend,
        Func<CancellationToken, Task<IBuilderJobExecution>> capture, CancellationToken cancellationToken = default)
    {
        if (!admission.TryBind(this)) throw new InvalidOperationException("A Builder admission object cannot be replayed.");
        try { return await AdmitAndStartCoreAsync(admission, backend, capture, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { admission.Failure = error; throw; }
        finally { admission.BoundaryClosed = true; } // Actual accepted callback/gate has drained; no later Start continuation exists.
    }

    private async Task<string> AdmitAndStartCoreAsync(BuilderJobAdmission admission, IBuilderBackend backend,
        Func<CancellationToken, Task<IBuilderJobExecution>> capture, CancellationToken cancellationToken)
    {
        string? accepted = null;
        Exception? startFailure = null;
        CancellationToken invocationToken = default;
        CancellationToken preparationToken = default;
        var preparationExpired = false;
        cancellationToken.ThrowIfCancellationRequested();
        // Register without the caller token: accepted persistence may finish a
        // positive local no-Start record after caller cancellation. Invocation,
        // preparation and data work link their actual tokens explicitly below.
        await _updates.RunAsync(UpdateKey, async context =>
        {
            var began = Stopwatch.GetTimestamp();
            using var data = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            data.CancelAfter(Budget);
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, context.CancellationToken);
            using var preparation = CancellationTokenSource.CreateLinkedTokenSource(invocation.Token, data.Token);
            var token = preparationToken = preparation.Token;
            var dataToken = data.Token;
            invocationToken = invocation.Token;
            try
            {
                await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
                var document = await ReadAsync(token).ConfigureAwait(false);
                if (document.Pending.Any(row => row.Reservation.JobId == admission.Request.Id)
                    || document.Completed.Any(row => row.Reservation.JobId == admission.Request.Id))
                    throw new InvalidOperationException("This Builder job identity is already journaled. Reconcile it; never replay Start.");
                var scope = BuilderJobExclusionScope.Create(admission.Request);
                foreach (var old in document.Pending.Where(row => row.Reservation.Scope.Overlaps(scope)).ToArray())
                {
                    BuilderJobStopResult stopped;
                    try
                    {
                        stopped = await ObserveHistoricalAsync(old, backend, async recovered =>
                        { document = await RecordRecoveryBindingHeldAsync(document, old.Reservation, recovered, dataToken).ConfigureAwait(false); }, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception error) when (error is not BuilderJournalPersistenceException) { startFailure = error; return; }
                    document = await CompleteHeldAsync(document, old.Reservation, stopped, dataToken).ConfigureAwait(false);
                }
                if (document.Pending.Length >= MaximumPending) throw new IOException("The Builder admission journal is full. Reconcile existing ownership before starting another job.");

                token.ThrowIfCancellationRequested();
                IBuilderJobExecution execution;
                try
                {
                    execution = await capture(token).ConfigureAwait(false);
                    token.ThrowIfCancellationRequested();
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception error) { startFailure = error; return; }
                admission.Execution = execution;
                admission.Request = admission.Request with { BackendId = execution.BackendId };
                admission.Reservation = BuilderJobReservation.Create(admission.Request, execution);
                if (admission.Reservation.Scope != scope)
                    throw new InvalidDataException("The project's physical admission root changed during capture. No Start was attempted; inspect the source before retrying.");
                var next = document with { Pending = [.. document.Pending, new(admission.Reservation)] };
                // Accepted data work survives caller cancellation, but shares the
                // same bounded pre-Start budget and never survives revocation.
                document = await WriteAsync(document, next, dataToken, () => admission.PendingWriteAttempted = true).ConfigureAwait(false);

                var readback = await ReadAsync(dataToken).ConfigureAwait(false);
                if (readback.Revision != document.Revision
                    || readback.Pending.SingleOrDefault(row => row.Reservation.Generation == admission.Reservation.Generation)?.Reservation != admission.Reservation)
                    throw new InvalidDataException("Builder admission readback no longer matches its exact reservation.");
                Exception? localNoStart = token.IsCancellationRequested ? new OperationCanceledException(token) : null;
                if (localNoStart is null)
                {
                    try
                    {
                        if (BuilderJobExclusionScope.Create(admission.Request) != admission.Reservation.Scope)
                            localNoStart = new InvalidDataException("The project's physical root changed after journal persistence. No Start was attempted.");
                    }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException or InvalidDataException)
                    { localNoStart = error; }
                }
                if (localNoStart is not null)
                {
                    var stopped = BuilderJobStopResult.NotStarted(admission.Reservation.JobId, localNoStart.Message);
                    admission.HasPositiveNoStartProof = true;
                    await CompleteHeldAsync(document, admission.Reservation, stopped, dataToken, allowNotStarted: true).ConfigureAwait(false);
                    admission.StopResult = stopped;
                    startFailure = localNoStart;
                    return;
                }

                // Pause only this non-Start budget, retaining its unused portion
                // for positive no-dispatch persistence. Start keeps its existing
                // 30s private RPC bound and the gate awaits its ACTUAL task.
                var remainingPersistence = Budget - Stopwatch.GetElapsedTime(began);
                if (remainingPersistence <= TimeSpan.Zero) throw new TimeoutException("Builder preparation exhausted its shared 12-second budget before Start.");
                data.CancelAfter(Timeout.InfiniteTimeSpan);
                token.ThrowIfCancellationRequested();
                admission.StartAttempted = true;
                try
                {
                    accepted = await execution.StartJobAsync(admission.Request, invocationToken).ConfigureAwait(false);
                    if (accepted != admission.Reservation.JobId) throw new InvalidDataException("Builder returned a different job identity from its sole Start attempt.");
                }
                catch (Exception error) when (execution.IsKnownNotStarted(error))
                {
                    var stopped = BuilderJobStopResult.NotStarted(admission.Reservation.JobId, error.Message);
                    admission.HasPositiveNoStartProof = true;
                    data.CancelAfter(remainingPersistence); // No renewed 12s grant after Start.
                    try { await CompleteHeldAsync(document, admission.Reservation, stopped, dataToken, allowNotStarted: true).ConfigureAwait(false); }
                    catch (Exception persistenceError)
                    { throw new AggregateException("Builder Start was rejected before acceptance, but its journal completion did not acknowledge persistence.", error, persistenceError); }
                    admission.StopResult = stopped;
                    admission.StartAttempted = false;
                    startFailure = error;
                }
                catch (Exception error) { startFailure = error; } // Durable PendingStart remains; no replay.
            }
            catch (OperationCanceledException error) when (cancellationToken.IsCancellationRequested
                && (error.CancellationToken == token || error.CancellationToken == dataToken || error.CancellationToken == invocationToken))
            { throw new OperationCanceledException(error.Message, error, cancellationToken); }
            catch (OperationCanceledException error) when (context.CancellationToken.IsCancellationRequested
                && (error.CancellationToken == token || error.CancellationToken == dataToken || error.CancellationToken == invocationToken))
            { throw new OperationCanceledException(error.Message, error, context.CancellationToken); }
            catch (OperationCanceledException error) when (data.IsCancellationRequested
                && (error.CancellationToken == token || error.CancellationToken == dataToken))
            { throw new TimeoutException("Builder preparation exceeded its shared 12-second gate/read/capture/persistence budget. No Start may be replayed.", error); }
            finally { preparationExpired = data.IsCancellationRequested; }
        }).ConfigureAwait(false);
        if (startFailure is OperationCanceledException cancellation && cancellationToken.IsCancellationRequested
            && (cancellation.CancellationToken == invocationToken || cancellation.CancellationToken == preparationToken))
            throw new OperationCanceledException(cancellation.Message, cancellation, cancellationToken);
        if (startFailure is OperationCanceledException expired && preparationExpired && expired.CancellationToken == preparationToken)
            throw new TimeoutException("Builder preparation exceeded its shared 12-second budget.", expired);
        if (startFailure is not null) ExceptionDispatchInfo.Capture(startFailure).Throw();
        return accepted ?? throw new InvalidDataException("Builder Start completed without an accepted identity.");
    }

    public async Task<BuilderJobStopResult> ReconcileAsync(string jobId, IBuilderBackend backend, CancellationToken cancellationToken = default)
    {
        BuilderJobStopResult? result = null;
        await AcceptedAsync(async token =>
        {
            await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
            var document = await ReadAsync(token).ConfigureAwait(false);
            var completed = document.Completed.SingleOrDefault(row => row.Reservation.JobId == jobId);
            if (completed is not null) { result = completed.Result(); return; }
            var pending = document.Pending.SingleOrDefault(row => row.Reservation.JobId == jobId)
                ?? throw new IOException("No Builder admission with this identity is retained in the journal.");
            result = await ObserveHistoricalAsync(pending, backend, async recovered =>
            { document = await RecordRecoveryBindingHeldAsync(document, pending.Reservation, recovered, token).ConfigureAwait(false); }, token).ConfigureAwait(false);
            await CompleteHeldAsync(document, pending.Reservation, result, token).ConfigureAwait(false);
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    public async Task<BuilderJobStopResult> CompleteAsync(BuilderJobReservation reservation, BuilderJobStopResult result, CancellationToken cancellationToken = default)
    {
        BuilderJobStopResult? acknowledged = null;
        await AcceptedAsync(async token =>
        {
            await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
            var document = await CompleteHeldAsync(await ReadAsync(token).ConfigureAwait(false), reservation, result, token).ConfigureAwait(false);
            acknowledged = document.Completed.Single(row => row.Reservation == reservation).Result();
        }, cancellationToken).ConfigureAwait(false);
        return acknowledged!;
    }

    public async Task<BuilderJobStopResult?> ReadCompletedAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default)
    {
        BuilderJobStopResult? result = null;
        await AcceptedAsync(async token =>
        {
            await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
            var document = await ReadAsync(token).ConfigureAwait(false);
            var completed = document.Completed.SingleOrDefault(row => row.Reservation.Generation == reservation.Generation);
            if (completed is null) return;
            if (completed.Reservation != reservation) throw new InvalidDataException("Builder retained completion belongs to another admission generation.");
            result = completed.Result();
        }, cancellationToken).ConfigureAwait(false);
        return result;
    }

    public async Task<BuilderJobStopResult> CompleteOwnedAsync(BuilderJobAdmission admission, BuilderJobStopResult result,
        CancellationToken cancellationToken = default)
    {
        if (!ReferenceEquals(admission.Owner, this) || !admission.BoundaryClosed || !admission.PendingWriteAttempted
            || !admission.StartAttempted || admission.HasPositiveNoStartProof || admission.Reservation is not { } reservation)
            throw new InvalidOperationException("No drained original Builder admission owns this completion receipt.");
        BuilderJobStopResult? acknowledged = null;
        await AcceptedAsync(async token =>
        {
            await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
            var document = await ReadAsync(token).ConfigureAwait(false);
            RejectReservationConflict(document, reservation);
            if (result.JobId != reservation.JobId) throw new InvalidDataException("Builder completion identifies another job.");
            if (result.Outcome == BuilderJobStopOutcome.KnownTerminal) ValidateSnapshot(reservation, result.TerminalSnapshot!);
            if (admission.TerminalReceipt is { } retained)
            {
                if (retained.Reservation != reservation) throw new InvalidDataException("The original Builder terminal receipt belongs to another exact reservation.");
                if (result.Outcome == BuilderJobStopOutcome.KnownTerminal && result.TerminalSnapshot!.State != retained.Result.TerminalSnapshot!.State)
                    throw new InvalidDataException("The original Builder terminal receipt contradicts the new result.");
                result = retained.Result; // Never replace a known negative business outcome with cleanup/unknown.
            }
            if (!document.Pending.Any(row => row.Reservation == reservation)
                && !document.Completed.Any(row => row.Reservation == reservation))
            {
                // The sole missing-row exception needs an existing live receipt,
                // not a supplied terminal snapshot or a missing old Worker job.
                // No pending row (including a new unrelated generation) is removed.
                var receipt = admission.TerminalReceipt;
                if (receipt is null || document.Revision <= receipt.BeforeRevision)
                    throw new InvalidDataException("The exact Builder completion is absent and no original-owner compaction receipt proves it.");
                document = await WriteAsync(document, document with { Completed = document.Completed.Append(new BuilderJournalCompleted(
                    reservation, BuilderJobStopOutcome.KnownTerminal, Bound(receipt.Result.TerminalSnapshot!), null,
                    receipt.Result.LastFailure)).TakeLast(MaximumHistory).ToArray() }, token).ConfigureAwait(false);
            }
            else
            {
                document = await CompleteHeldAsync(document, reservation, result, token, rememberTerminal:
                    result.Outcome == BuilderJobStopOutcome.KnownTerminal
                        ? () => { admission.TerminalReceipt ??= new(reservation, document.Revision, BuilderJobStopResult.KnownTerminal(
                            Bound(result.TerminalSnapshot!), result.LastFailure
                                ?? document.Pending.SingleOrDefault(row => row.Reservation == reservation)?.LastFailure
                                ?? document.Completed.SingleOrDefault(row => row.Reservation == reservation)?.LastFailure)); } : null).ConfigureAwait(false);
            }
            acknowledged = document.Completed.Single(row => row.Reservation == reservation).Result();
        }, cancellationToken).ConfigureAwait(false);
        return acknowledged!;
    }

    public async Task<BuilderJobStopResult> AbortKnownNotStartedAsync(BuilderJobAdmission admission, CancellationToken cancellationToken = default)
    {
        // This ephemeral proof cannot be reconstructed from a pending JSON row by
        // a successor. The original awaited boundary must have ended without Start,
        // or with independently trusted no-dispatch proof. Current data authority
        // is still required; the receipt grants no post-revocation privilege.
        if (!ReferenceEquals(admission.Owner, this) || !admission.BoundaryClosed
            || admission.StartAttempted && !admission.HasPositiveNoStartProof || !admission.PendingWriteAttempted
            || admission.Reservation is not { } reservation)
            throw new InvalidOperationException("No live original-activation no-start authority owns this Builder admission.");
        BuilderJobStopResult? result = null;
        await AcceptedAsync(async token =>
        {
            await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
            var document = await ReadAsync(token).ConfigureAwait(false);
            RejectReservationConflict(document, reservation);
            var completed = document.Completed.SingleOrDefault(row => row.Reservation == reservation);
            if (completed is not null) { result = completed.Result(); return; }
            var stopped = BuilderJobStopResult.NotStarted(reservation.JobId, admission.Failure?.Message);
            if (document.Pending.Any(row => row.Reservation == reservation))
                document = await CompleteHeldAsync(document, reservation, stopped, token, allowNotStarted: true).ConfigureAwait(false);
            else
                document = await WriteAsync(document, document with { Completed = document.Completed.Append(new BuilderJournalCompleted(
                    reservation, BuilderJobStopOutcome.NotStarted, null, null, stopped.LastFailure)).TakeLast(MaximumHistory).ToArray() }, token).ConfigureAwait(false);
            result = document.Completed.Single(row => row.Reservation == reservation).Result();
        }, cancellationToken).ConfigureAwait(false);
        return result!;
    }

    public Task SavePartialAsync(BuilderJobReservation reservation, BuilderJobSnapshot snapshot, CancellationToken cancellationToken = default)
        => AcceptedAsync(async token =>
        {
            await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
            var document = await ReadAsync(token).ConfigureAwait(false);
            var old = document.Pending.SingleOrDefault(row => row.Reservation.Generation == reservation.Generation);
            if (old is null)
            {
                if (document.Completed.Any(row => row.Reservation == reservation)) return;
                throw new InvalidDataException("The exact Builder reservation disappeared before partial-result persistence.");
            }
            if (old.Reservation != reservation) throw new InvalidDataException("Builder partial-result ownership changed.");
            ValidateSnapshot(reservation, snapshot);
            var bounded = Bound(snapshot);
            if (JsonSerializer.Serialize(old.LastSnapshot, Json) == JsonSerializer.Serialize(bounded, Json)) return;
            var next = document with { Pending = document.Pending.Select(row => row == old ? row with { LastSnapshot = bounded } : row).ToArray() };
            await WriteAsync(document, next, token).ConfigureAwait(false);
        }, cancellationToken);

    public Task SaveFailureAsync(BuilderJobReservation reservation, string failure, CancellationToken cancellationToken = default)
        => AcceptedAsync(async token =>
        {
            await using var gate = await _storage.EnterAsync(token).ConfigureAwait(false);
            var document = await ReadAsync(token).ConfigureAwait(false);
            var old = document.Pending.SingleOrDefault(row => row.Reservation == reservation);
            if (old is null)
            {
                if (document.Completed.Any(row => row.Reservation == reservation)) return;
                throw new InvalidDataException("The exact Builder reservation disappeared before failure persistence.");
            }
            var bounded = failure.Length > 2048 ? failure[..2048] : failure;
            if (old.LastFailure == bounded) return;
            await WriteAsync(document, document with
            { Pending = document.Pending.Select(row => row == old ? row with { LastFailure = bounded } : row).ToArray() }, token).ConfigureAwait(false);
        }, cancellationToken);

    private async Task AcceptedAsync(Func<CancellationToken, Task> operation, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var deadline = new CancellationTokenSource(Budget);
        // Registration precedes all workspace/file access. Await actual callback
        // completion even when the caller/deadline cancels. Explicit linking is
        // required regardless of RunAsync's admission-token propagation semantics.
        // This is ONE deadline for gate/read/recovery/write/readback, not 12s each.
        await _updates.RunAsync(UpdateKey, async context =>
        {
            using var bounded = CancellationTokenSource.CreateLinkedTokenSource(token, context.CancellationToken, deadline.Token);
            try
            {
                bounded.Token.ThrowIfCancellationRequested();
                await operation(bounded.Token).ConfigureAwait(false);
                bounded.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException error) when (token.IsCancellationRequested && error.CancellationToken == bounded.Token)
            { throw new OperationCanceledException(error.Message, error, token); }
            catch (OperationCanceledException error) when (context.CancellationToken.IsCancellationRequested && error.CancellationToken == bounded.Token)
            { throw new OperationCanceledException(error.Message, error, context.CancellationToken); }
            catch (OperationCanceledException error) when (deadline.IsCancellationRequested && error.CancellationToken == bounded.Token)
            { throw new TimeoutException("Builder journal operation exceeded its shared 12-second gate/read/recovery/persistence budget. Its actual callback was drained; no Start may be replayed.", error); }
        }, token).ConfigureAwait(false);
    }

    private static async Task<BuilderJobStopResult> ObserveHistoricalAsync(BuilderJournalPending row, IBuilderBackend backend,
        Func<BuilderRecordedJobRecovery, Task> rememberBinding, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (row.LastSnapshot is { State: "succeeded" or "failed" or "cancelled" } terminal)
        {
            ValidateSnapshot(row.Reservation, terminal);
            return BuilderJobStopResult.KnownTerminal(terminal); // Previously acknowledged exact terminal read, not replacement readiness.
        }
        var last = row.LastSnapshot;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                BuilderRecordedJobRecovery? recovered = null;
                try { recovered = await backend.RecoverRecordedJobAsync(row.Reservation, token).ConfigureAwait(false); }
                catch (Exception error) when ((error is IOException or HttpRequestException or TimeoutException)
                    && error is not InvalidDataException) { }
                token.ThrowIfCancellationRequested();
                var snapshot = recovered?.Snapshot;
                if (recovered is not null)
                {
                    ValidateRecoveryBinding(row.Reservation, new(recovered.BindingIdentity, recovered.ExecutionReference, recovered.BackendId));
                    if (snapshot is not null) ValidateSnapshot(row.Reservation, snapshot);
                    await rememberBinding(recovered).ConfigureAwait(false);
                }
                if (snapshot is not null)
                {
                    ValidateSnapshot(row.Reservation, snapshot);
                    last = snapshot;
                    if (snapshot.State != "running") return BuilderJobStopResult.KnownTerminal(snapshot);
                }
                var observed = await backend.ObserveExecutionAsync(row.Reservation.ExecutionReference, token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                if (observed.Reference != row.Reservation.ExecutionReference || !Enum.IsDefined(observed.State))
                    throw new InvalidDataException("Historical Builder cleanup evidence identifies another execution.");
                if (observed.State == PackageWorkerExecutionState.CleanupConfirmed)
                    return BuilderJobStopResult.ExecutionCleaned(row.Reservation.JobId, last, observed, row.LastFailure);
                if (observed.State == PackageWorkerExecutionState.Unknown)
                    throw new IOException("The original Builder admission has no exact terminal or execution-cleanup proof. Reconcile it before changing this project or SDK.");
            }
            catch (SunderRpcException error) when (((error.Error.Kind is SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.ResourceExhausted)
                    && error.Error.Code != "rpc.worker-execution.unsupported")
                || error.Error is { Kind: SunderRpcErrorKind.StaleEndpoint, Code: "rpc.caller-retired" }) { }
            await Task.Delay(100, token).ConfigureAwait(false);
        }
    }

    private async Task<BuilderJournalDocument> RecordRecoveryBindingHeldAsync(BuilderJournalDocument document,
        BuilderJobReservation reservation, BuilderRecordedJobRecovery recovered, CancellationToken token)
    {
        var bridge = new BuilderJournalBindingBridge(recovered.BindingIdentity, recovered.ExecutionReference, recovered.BackendId);
        ValidateRecoveryBinding(reservation, bridge);
        var old = document.Pending.SingleOrDefault(row => row.Reservation == reservation)
            ?? throw new InvalidDataException("Builder recovery no longer owns its exact pending reservation.");
        var snapshot = recovered.Snapshot is { } latest ? Bound(latest) : old.LastSnapshot;
        if (old.RecoveryBinding == bridge && JsonSerializer.Serialize(old.LastSnapshot, Json) == JsonSerializer.Serialize(snapshot, Json)) return document;
        try
        {
            return await WriteAsync(document, document with
            { Pending = document.Pending.Select(row => row == old ? row with { RecoveryBinding = bridge, LastSnapshot = snapshot } : row).ToArray() }, token).ConfigureAwait(false);
        }
        catch (Exception error) { throw new BuilderJournalPersistenceException(error); }
    }

    private async Task<BuilderJournalDocument> CompleteHeldAsync(BuilderJournalDocument document, BuilderJobReservation reservation,
        BuilderJobStopResult result, CancellationToken token, bool allowNotStarted = false, Action? rememberTerminal = null)
    {
        RejectReservationConflict(document, reservation);
        if (result.JobId != reservation.JobId) throw new InvalidDataException("Builder completion identifies another job.");
        var prior = document.Completed.SingleOrDefault(row => row.Reservation.Generation == reservation.Generation);
        if (prior is not null)
        {
            if (prior.Reservation != reservation) throw new InvalidDataException("Builder completion generation was reused.");
            if (result.Outcome == BuilderJobStopOutcome.KnownTerminal)
            {
                ValidateSnapshot(reservation, result.TerminalSnapshot!);
                if (prior.Outcome == BuilderJobStopOutcome.NotStarted
                    || prior.Outcome == BuilderJobStopOutcome.KnownTerminal && prior.LastSnapshot!.State != result.TerminalSnapshot!.State)
                    throw new InvalidDataException("Builder returned contradictory positive job outcomes for the same exact admission.");
                if (prior.Outcome == BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome)
                {
                    rememberTerminal?.Invoke();
                    return await WriteAsync(document, document with { Completed = document.Completed.Select(row => row == prior
                        ? row with { Outcome = BuilderJobStopOutcome.KnownTerminal, LastSnapshot = Bound(result.TerminalSnapshot!), Observation = null } : row).ToArray() }, token).ConfigureAwait(false);
                }
            }
            return document;
        }
        var pending = document.Pending.SingleOrDefault(row => row.Reservation.Generation == reservation.Generation);
        if (pending?.Reservation != reservation) throw new InvalidDataException("The exact pending Builder reservation is missing or changed.");
        if (result.Outcome == BuilderJobStopOutcome.NotStarted && !allowNotStarted)
            throw new InvalidDataException("Only the held Start boundary may persist positive no-start authority.");
        if (result.Outcome == BuilderJobStopOutcome.KnownTerminal)
        {
            ValidateSnapshot(reservation, result.TerminalSnapshot!);
            if (result.TerminalSnapshot!.State == "running") throw new InvalidDataException("Running is not a terminal Builder outcome.");
        }
        if (result.Outcome == BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome
            && (result.ExecutionObservation?.Reference != reservation.ExecutionReference
                || result.ExecutionObservation.State != PackageWorkerExecutionState.CleanupConfirmed))
            throw new InvalidDataException("Only positive exact Host cleanup evidence can release uncertain execution ownership.");
        var completed = new BuilderJournalCompleted(reservation, result.Outcome,
            result.LastSnapshot is { } last ? Bound(last) : pending.LastSnapshot, result.ExecutionObservation,
            result.LastFailure ?? pending.LastFailure, pending.RecoveryBinding);
        var next = document with
        {
            Pending = document.Pending.Where(row => row != pending).ToArray(),
            Completed = document.Completed.Append(completed).TakeLast(MaximumHistory).ToArray(),
        };
        rememberTerminal?.Invoke();
        return await WriteAsync(document, next, token).ConfigureAwait(false);
    }

    private static void RejectReservationConflict(BuilderJournalDocument document, BuilderJobReservation reservation)
    {
        if (document.Pending.Select(row => row.Reservation).Concat(document.Completed.Select(row => row.Reservation))
            .Any(row => (row.Generation == reservation.Generation || row.JobId == reservation.JobId) && row != reservation))
            throw new InvalidDataException("The Builder job/generation now belongs to a different exact reservation. No row was changed.");
    }

    private async Task<BuilderJournalDocument> WriteAsync(BuilderJournalDocument before, BuilderJournalDocument next, CancellationToken token,
        Action? beforeStorageWrite = null)
    {
        token.ThrowIfCancellationRequested();
        next = next with { Revision = checked(before.Revision + 1) };
        Validate(next);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(next, Json);
        if (bytes.Length > MaximumDocumentBytes) throw new IOException("Builder admission journal exceeds its 1 MiB bound. Pending ownership was preserved; no new Start is allowed.");
        beforeStorageWrite?.Invoke();
        await _storage.WriteAsync(bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var readback = await _storage.ReadAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (readback is null || !bytes.AsSpan().SequenceEqual(readback))
            throw new IOException("Builder journal write did not confirm exact readback. Pending ownership remains uncertain; do not replay Start.");
        return Parse(readback);
    }

    private async Task<BuilderJournalDocument> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var bytes = await _storage.ReadAsync(token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Builder admission document is missing; only storage's positively proven one-time initialization may create an empty store.");
        token.ThrowIfCancellationRequested();
        return Parse(bytes);
    }

    private static BuilderJournalDocument Parse(byte[] bytes)
    {
        if (bytes.Length > MaximumDocumentBytes) throw new InvalidDataException("Builder admission journal exceeds its 1 MiB bound and was preserved.");
        using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        RejectDuplicateKeys(json.RootElement);
        var document = json.RootElement.Deserialize<BuilderJournalDocument>(Json) ?? throw new InvalidDataException("Builder admission journal is empty.");
        Validate(document);
        return document;
    }

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            HashSet<string> names = new(StringComparer.Ordinal);
            foreach (var member in value.EnumerateObject())
            {
                if (!names.Add(member.Name)) throw new InvalidDataException("Builder admission journal contains duplicate fields.");
                RejectDuplicateKeys(member.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    private static void Validate(BuilderJournalDocument document)
    {
        if (document.Schema != "sunder.builder.admission" || document.Version != 1 || document.Revision < 0
            || document.Pending is null || document.Completed is null || document.Pending.Length > MaximumPending || document.Completed.Length > MaximumHistory)
            throw new InvalidDataException("Builder admission journal schema, version or bounds are invalid. The file was preserved.");
        var reservations = document.Pending.Select(row => row?.Reservation).Concat(document.Completed.Select(row => row?.Reservation)).ToArray();
        if (reservations.Any(row => row is null) || reservations.Select(row => row!.Generation).Distinct().Count() != reservations.Length
            || reservations.Select(row => row!.JobId).Distinct(StringComparer.Ordinal).Count() != reservations.Length)
            throw new InvalidDataException("Builder journal contains missing or duplicate ownership identities.");
        foreach (var row in reservations)
        {
            var p = row!.ProviderIdentity;
            if (row.Generation == Guid.Empty || string.IsNullOrWhiteSpace(row.JobId) || row.JobId.Length > 128
                || !Guid.TryParseExact(row.BackendId, "N", out _) || string.IsNullOrWhiteSpace(row.Operation) || row.Operation.Length > 64
                || !Enum.IsDefined(row.Kind) || row.Phase != BuilderJobJournalPhase.PendingStart || !row.DispatchMayHaveHappened
                || row.Scope is null || (!row.Scope.SdkGlobal && row.Scope.PhysicalProjectRoot is null)
                || row.Scope.PhysicalProjectRoot is { } root && (!Path.IsPathFullyQualified(root) || root.Length > 8192
                    || Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) != root)
                || !Digest(row.RequestSha256) || row.ExecutionReference is null || p is null
                || p.PackageId != BuilderViewModel.BuilderPackageId || !SemanticVersion.TryParse(p.PackageVersion, out _)
                || !PackageId.TryParse(p.ProviderId, out _) || !PackageId.TryParse(p.ContractId, out _) || !SemanticVersion.TryParse(p.ContractVersion, out _)
                || !Digest(p.DescriptorSha256) || !Digest(p.AbiSha256) || p.ActivationId == Guid.Empty
                || p.ActivationEpoch is < 0 or > 9_007_199_254_740_991 || p.SessionGeneration is < 0 or > 9_007_199_254_740_991)
                throw new InvalidDataException("Builder journal ownership metadata is invalid. The file was preserved.");
        }
        foreach (var pending in document.Pending)
        {
            if (pending.LastSnapshot is { } snapshot) ValidateSnapshot(pending.Reservation, snapshot);
            if (pending.LastFailure is { Length: > 2048 }) throw new InvalidDataException("Builder journal failure metadata exceeds its bound.");
            if (pending.RecoveryBinding is { } bridge) ValidateRecoveryBinding(pending.Reservation, bridge);
        }
        foreach (var completed in document.Completed)
        {
            if (!Enum.IsDefined(completed.Outcome)) throw new InvalidDataException("Builder journal contains an invalid stop authority.");
            if (completed.LastFailure is { Length: > 2048 }) throw new InvalidDataException("Builder journal failure metadata exceeds its bound.");
            if (completed.RecoveryBinding is { } bridge) ValidateRecoveryBinding(completed.Reservation, bridge);
            if (completed.LastSnapshot is { } snapshot) ValidateSnapshot(completed.Reservation, snapshot);
            if (completed.Outcome == BuilderJobStopOutcome.KnownTerminal
                    && (completed.LastSnapshot?.State is not ("succeeded" or "failed" or "cancelled") || completed.Observation is not null)
                || completed.Outcome == BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome
                    && (completed.Observation?.Reference != completed.Reservation.ExecutionReference || completed.Observation.State != PackageWorkerExecutionState.CleanupConfirmed)
                || completed.Outcome == BuilderJobStopOutcome.NotStarted && (completed.LastSnapshot is not null || completed.Observation is not null))
                throw new InvalidDataException("Builder journal completion proof is inconsistent.");
        }
    }

    private static bool Digest(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private static void ValidateRecoveryBinding(BuilderJobReservation reservation, BuilderJournalBindingBridge bridge)
    {
        var binding = bridge.ObservedBinding;
        if (binding is null || !reservation.ProviderIdentity.HasSameExecutionCandidate(binding)
            || binding.SessionGeneration is < 0 or > 9_007_199_254_740_991
            || bridge.ExecutionReference != reservation.ExecutionReference || bridge.BackendId != reservation.BackendId)
            throw new InvalidDataException("Builder recovery binding lacks an exact original execution/backend bridge.");
    }
    internal static void ValidateSnapshot(BuilderJobReservation reservation, BuilderJobSnapshot snapshot)
    {
        if (snapshot.Id != reservation.JobId || snapshot.BackendId != reservation.BackendId || snapshot.Operation != reservation.Operation
            || snapshot.State is not ("running" or "succeeded" or "failed" or "cancelled") || snapshot.Message is null || snapshot.Output is null)
            throw new InvalidDataException("Builder journal received a snapshot from a different job, backend or operation.");
    }
    private static BuilderJobSnapshot Bound(BuilderJobSnapshot snapshot)
    {
        var bounded = snapshot with
        {
            Output = snapshot.Output.Length > 2048 ? snapshot.Output[^2048..] : snapshot.Output,
            Message = snapshot.Message.Length > 1024 ? snapshot.Message[..1024] : snapshot.Message,
            Error = snapshot.Error is { Length: > 2048 } error ? error[..2048] : snapshot.Error,
        };
        if (JsonSerializer.SerializeToUtf8Bytes(bounded, Json).Length > 16 * 1024)
            throw new IOException("Builder partial source metadata exceeds its journal bound; existing ownership was preserved.");
        return bounded;
    }

    private sealed class BuilderJournalPersistenceException(Exception inner)
        : IOException("Builder recovery observed an exact binding, but its journal update did not acknowledge persistence.", inner);
}
