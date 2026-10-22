extern alias BuilderApp;

using System.Text;
using System.Text.Json;
using BuilderApp::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Logging;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderServiceTests
{
    [Fact]
    public async Task AcceptedJobRevocationCannotPersistStopOrReleaseItsDurablePendingOwnership()
    {
        using var revocation = new CancellationTokenSource();
        var updates = new JournalStateUpdates(revocation.Token);
        var storage = new BuilderMemoryJournalStorage();
        var journal = new BuilderJobJournal(storage, updates);
        var backend = new BuilderUiBackend();
        var observing = BuilderUiFixture.Signal();
        backend.BeforeGet = async (_, token) =>
        {
            if (!token.CanBeCanceled) throw new InvalidOperationException("Job observation must be cancellable.");
            observing.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        var application = new BuilderProjectApplicationService(backend, new BuilderUiStore(), new(new BuilderUiBackgroundQueue()), journal);
        var request = JournalRequest("revoked-live-job", Path.GetTempPath()) with { Operation = "watch" };
        var run = application.RunJobAsync(request);
        await observing.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("running", backend.Jobs[request.Id!].State);
        var writes = storage.Writes;
        backend.BeforeGet = null; // Subsequent independent cleanup may read a real exact terminal result.
        await revocation.CancelAsync();
        Assert.NotNull(await Xunit.Record.ExceptionAsync(() => run.WaitAsync(TimeSpan.FromSeconds(3))));
        Assert.Equal(writes, storage.Writes);
        Assert.Equal(request.Id, Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray())
            .GetProperty("reservation").GetProperty("jobId").GetString());
        Assert.Empty(ReadJournal(storage).GetProperty("completed").EnumerateArray());
        Assert.False(application.IsJobStopConfirmed(request.Id));
        Assert.NotNull(await Xunit.Record.ExceptionAsync(() => application.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3))));
        Assert.Equal(writes, storage.Writes);
    }

    [Fact]
    public async Task EscapedAcceptedJobContinuationCannotReenterJournalAfterOriginalCallbackCompletes()
    {
        var storage = new BuilderMemoryJournalStorage();
        var updates = new BuilderUiStateUpdates();
        var journal = new BuilderJobJournal(storage, updates);
        var admission = new BuilderJobAdmission(JournalRequest("escaped-job", Path.GetTempPath()));
        var release = BuilderUiFixture.Signal();
        var releaseSuccessor = BuilderUiFixture.Signal();
        Task<BuilderJobStopResult?>? escaped = null;
        Task<BuilderJobSnapshot>? successor = null;
        var terminal = await journal.RunOwnedAsync(async scope =>
        {
            await journal.AdmitAndStartAsync(admission, new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution()), scope: scope);
            var snapshot = new BuilderJobSnapshot { Id = admission.Request.Id!, BackendId = admission.Request.BackendId,
                Operation = admission.Request.Operation, State = "succeeded", Message = "done", Output = string.Empty };
            await journal.CompleteOwnedAsync(admission, BuilderJobStopResult.KnownTerminal(snapshot), scope: scope);
            escaped = Task.Run(async () =>
            {
                await release.Task;
                return await journal.ReadCompletedAsync(admission.Reservation!, scope: scope);
            });
            successor = Task.Run(async () =>
            {
                await releaseSuccessor.Task;
                return await journal.RunOwnedAsync(async next =>
                {
                    Assert.NotSame(scope, next);
                    Assert.NotNull(await journal.ReadCompletedAsync(admission.Reservation!, scope: next));
                    return snapshot;
                });
            });
            return snapshot;
        });
        Assert.Equal("succeeded", terminal.State);
        var reads = storage.Entries;
        var writes = storage.Writes;
        var accepted = updates.Accepted.Count;
        release.SetResult();
        await Assert.ThrowsAsync<InvalidOperationException>(() => escaped!.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(reads, storage.Entries);
        Assert.Equal(writes, storage.Writes);
        Assert.Equal(accepted, updates.Accepted.Count);
        releaseSuccessor.SetResult();
        Assert.Equal("succeeded", (await successor!.WaitAsync(TimeSpan.FromSeconds(3))).State);
        Assert.Equal(accepted + 1, updates.Accepted.Count);
    }

    [Fact]
    public async Task RecreatedFileJournalRejectsMissingInitializedDocumentBeforeCaptureAndPreservesGateAndTemporaryEvidence()
    {
        var root = Path.Combine(Path.GetTempPath(), "builder-journal-missing", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var context = new JournalContext(root);
            var execution = new JournalExecution();
            await new BuilderJobJournal(context, new BuilderUiStateUpdates()).AdmitAndStartAsync(
                new(JournalRequest("missing-document-original", root)), new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(execution));
            var directory = Path.Combine(root, BuilderJobJournalFiles.DirectoryName);
            var document = Path.Combine(directory, BuilderJobJournalFiles.DocumentName);
            var gate = Path.Combine(directory, "admission.lock");
            var marker = await File.ReadAllBytesAsync(gate);
            Assert.Equal(BuilderJobJournalFiles.InitializedMarker, Encoding.UTF8.GetString(marker));
            var temporary = Path.Combine(directory, ".journal-preserved.tmp");
            await File.WriteAllTextAsync(temporary, "uncommitted diagnostic evidence");
            File.Delete(document); // ONLY the document is lost, not the stable initialized gate.
            var captures = 0;
            var successorExecution = new JournalExecution();

            await Assert.ThrowsAsync<InvalidDataException>(() => new BuilderJobJournal(context, new BuilderUiStateUpdates()).AdmitAndStartAsync(
                new(JournalRequest("missing-document-overlap", Path.Combine(root, "child"))), new JournalBackend(), _ =>
                { captures++; return Task.FromResult<IBuilderJobExecution>(successorExecution); }));

            Assert.Equal(1, execution.Starts);
            Assert.Equal(0, captures);
            Assert.Equal(0, successorExecution.Starts);
            Assert.False(File.Exists(document));
            Assert.Equal(marker, await File.ReadAllBytesAsync(gate));
            Assert.Equal("uncommitted diagnostic evidence", await File.ReadAllTextAsync(temporary));
            Assert.Equal(2, Directory.EnumerateFileSystemEntries(directory).Count());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("new", true)]
    [InlineData("initializing-without-document", true)]
    [InlineData("initializing-with-empty-document", true)]
    [InlineData("torn-marker", false)]
    public async Task FirstStoreInitializationOrPreStartCrashRequiresPositiveGateEvidence(string phase, bool mayInitialize)
    {
        var root = Path.Combine(Path.GetTempPath(), "builder-journal-initialize", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var directory = Path.Combine(root, BuilderJobJournalFiles.DirectoryName);
            var gate = Path.Combine(directory, "admission.lock");
            if (phase != "new")
            {
                Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(gate, phase == "torn-marker" ? "" : BuilderJobJournalFiles.InitializingMarker);
                if (phase == "initializing-with-empty-document")
                    await File.WriteAllBytesAsync(Path.Combine(directory, BuilderJobJournalFiles.DocumentName),
                        JsonSerializer.SerializeToUtf8Bytes(BuilderJournalDocument.Empty, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            }
            var captures = 0;
            var execution = new JournalExecution();
            var work = new BuilderJobJournal(new JournalContext(root), new BuilderUiStateUpdates()).AdmitAndStartAsync(
                new(JournalRequest("initialize-" + phase, root)), new JournalBackend(), _ =>
                { captures++; return Task.FromResult<IBuilderJobExecution>(execution); });

            if (mayInitialize)
            {
                await work;
                Assert.Equal(1, captures);
                Assert.Equal(1, execution.Starts);
                Assert.Equal(BuilderJobJournalFiles.InitializedMarker, await File.ReadAllTextAsync(gate));
            }
            else
            {
                await Assert.ThrowsAsync<InvalidDataException>(() => work);
                Assert.Equal(0, captures);
                Assert.Equal(0, execution.Starts);
                Assert.Empty(await File.ReadAllTextAsync(gate));
                Assert.False(File.Exists(Path.Combine(directory, BuilderJobJournalFiles.DocumentName)));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task TwoJournalInstancesKeepOneStableFileGateThroughTheActualStartAcceptanceTask()
    {
        var root = Path.Combine(Path.GetTempPath(), "builder-journal-gate", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var context = new JournalContext(root);
            var first = new BuilderJobJournal(context, new BuilderUiStateUpdates());
            var second = new BuilderJobJournal(context, new BuilderUiStateUpdates());
            var backend = new JournalBackend();
            var entered = BuilderUiFixture.Signal();
            var release = BuilderUiFixture.Signal();
            var producer = new JournalExecution { Start = async (request, _) =>
            { entered.TrySetResult(); await release.Task; return request.Id!; } };
            var firstAdmission = new BuilderJobAdmission(JournalRequest("gate-first", root));
            var running = first.AdmitAndStartAsync(firstAdmission, backend, _ => Task.FromResult<IBuilderJobExecution>(producer));
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            using var cancellation = new CancellationTokenSource();
            var captures = 0;
            var blocked = second.AdmitAndStartAsync(new(JournalRequest("gate-second", Path.Combine(root, "child"))), backend, _ =>
            { Interlocked.Increment(ref captures); return Task.FromResult<IBuilderJobExecution>(new JournalExecution()); }, cancellation.Token);
            try
            {
                Assert.False(blocked.IsCompleted);
                Assert.Equal(0, captures);
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);
                Assert.False(running.IsCompleted); // Caller cancellation cannot release the other instance's gate.
            }
            finally { release.TrySetResult(); }
            await running.WaitAsync(TimeSpan.FromSeconds(3));
            Assert.True(File.Exists(Path.Combine(root, "builder-admission", "admission.lock")));
            var oldBytes = await File.ReadAllBytesAsync(Path.Combine(root, "builder-admission", "journal.v1.json"));
            Assert.Contains("gate-first", Encoding.UTF8.GetString(oldBytes));
            Assert.DoesNotContain("gate-second", Encoding.UTF8.GetString(oldBytes));
            Assert.Equal(1, producer.Starts);
            await Assert.ThrowsAsync<IOException>(() => second.AdmitAndStartAsync(
                new(JournalRequest("gate-third", Path.Combine(root, "child"))), backend, _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution())));
            Assert.Equal(oldBytes, await File.ReadAllBytesAsync(Path.Combine(root, "builder-admission", "journal.v1.json")));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task LostStartReplySurvivesNewApplicationAndBlocksCrossTechnologyChildUntilExactHostProof()
    {
        var storage = new BuilderMemoryJournalStorage();
        await using var original = new ObservedBuilderFixture(BuilderTestJournal.Create(storage));
        var source = BuilderUiFixture.Project("journal-recreated");
        original.Backend.LoseFirstStartResponse = true;
        var request = await original.MakeUncertainAsync(source);
        var oldReference = Assert.Single(original.Observations.Captured).Reference;
        original.ReplaceWithSameBackendId(request);
        var successorJournal = BuilderTestJournal.Create(storage); // Independent App/DI owner, shared durable data.
        await using var successor = new BuilderProjectApplicationService(original.Gateway, new BuilderUiStore(),
            new(new BuilderUiBackgroundQueue()), successorJournal);
        var child = new BuilderJobRequest { Id = "successor-child", Operation = "build", Kind = BuilderProjectKind.Node,
            ProjectFolder = Path.Combine(source.ProjectFolder, "child") };

        await Assert.ThrowsAsync<IOException>(() => successor.RunJobAsync(child));

        Assert.Single(original.Backend.Starts);
        Assert.Equal(0, original.ReplacementRuntime!.Reads);
        Assert.Equal(0, original.ReplacementRuntime.Cancels);
        Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        original.Observations.State = PackageWorkerExecutionState.CleanupConfirmed;
        var stopped = await successor.StopJobAsync(request.Id!);

        Assert.Equal(BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome, stopped.Outcome);
        Assert.Null(stopped.TerminalSnapshot);
        Assert.Equal(source, stopped.LastSnapshot?.Project);
        Assert.Equal(oldReference, stopped.ExecutionObservation?.Reference);
        Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        Assert.Contains(ReadJournal(storage).GetProperty("completed").EnumerateArray(),
            row => row.GetProperty("reservation").GetProperty("jobId").GetString() == request.Id);
        Assert.Equal("succeeded", (await successor.RunJobAsync(child with { Id = "successor-new-child" })).State);
        Assert.Single(original.Backend.Starts); // Still no replay of the uncertain original mutation.
    }

    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(true, true, true, false)]
    [InlineData(true, false, true, false)]
    [InlineData(true, true, false, false)]
    [InlineData(true, true, true, true)]
    public async Task SuccessorRecoversLiveWorkerOnlyThroughFreshExactExecutionAndBackendBridge(
        bool refreshedBinding, bool sameExecutionReference, bool sameBackend, bool runningJob)
    {
        var storage = new BuilderMemoryJournalStorage();
        await using var original = new ObservedBuilderFixture(BuilderTestJournal.Create(storage));
        var source = BuilderUiFixture.Project("live-worker-bridge");
        var request = await original.MakeUncertainAsync(source);
        var reference = Assert.Single(original.Observations.Captured).Reference;
        var issued = original.Catalog.Current;
        var runtime = Assert.IsType<BuilderRuntimeFake>(issued.Service);
        var reads = runtime.Reads;
        var cancels = runtime.Cancels;
        var backendReads = runtime.BackendIds;
        original.Backend.BeforeGet = null;
        original.Backend.BeforeCancel = null;
        if (!runningJob) original.Backend.Jobs[request.Id!] = original.Backend.Jobs[request.Id!] with
        { State = "failed", Message = "Original build failed", Error = "Original compiler failure" };
        if (!sameBackend) original.Backend.BackendId = new('b', 32);
        original.Catalog.Rebind(refreshedBinding);
        Assert.NotSame(issued, original.Catalog.Current); // Never revive the old issued SDK entry.
        var observations = new BuilderReceiptObservations(reference.RuntimeInstanceId)
        {
            State = PackageWorkerExecutionState.CleanupPending,
            CaptureReference = _ => sameExecutionReference ? reference : new(reference.RuntimeInstanceId, Guid.NewGuid().ToString("N")),
        };
        var gateway = new BuilderBackendGateway(original.Catalog, observations);
        var journal = BuilderTestJournal.Create(storage);
        await using var successor = new BuilderProjectApplicationService(gateway, new BuilderUiStore(), new(new BuilderUiBackgroundQueue()), journal);
        if (sameExecutionReference && sameBackend)
        {
            var stopped = await successor.StopJobAsync(request.Id!);

            Assert.Equal(BuilderJobStopOutcome.KnownTerminal, stopped.Outcome);
            Assert.Equal(runningJob ? "cancelled" : "failed", stopped.TerminalSnapshot?.State);
            if (!runningJob) Assert.Equal("Original compiler failure", stopped.TerminalSnapshot?.Error);
            Assert.Equal(source, stopped.TerminalSnapshot?.Project);
            Assert.Null(stopped.ExecutionObservation); // The whole Worker is still alive/Pending.
            Assert.Empty(observations.Observed);
            Assert.Equal(1, observations.CaptureAttempts);
            Assert.Equal(backendReads + 1, runtime.BackendIds);
            Assert.Equal(reads + (runningJob ? 2 : 1), runtime.Reads);
            Assert.Equal(cancels + (runningJob ? 1 : 0), runtime.Cancels);
            var completed = Assert.Single(ReadJournal(storage).GetProperty("completed").EnumerateArray(),
                row => row.GetProperty("reservation").GetProperty("jobId").GetString() == request.Id);
            Assert.Equal(issued.SessionGeneration, completed.GetProperty("reservation").GetProperty("providerIdentity").GetProperty("sessionGeneration").GetInt64());
            Assert.Equal(original.Catalog.Current.SessionGeneration, completed.GetProperty("recoveryBinding").GetProperty("observedBinding").GetProperty("sessionGeneration").GetInt64());
            Assert.Equal(10, completed.GetProperty("recoveryBinding").GetProperty("observedBinding").EnumerateObject().Count());
            Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        }
        else
        {
            using var cancellation = new CancellationTokenSource();
            observations.Observe = (old, _) =>
            {
                Assert.Equal(reference, old);
                cancellation.Cancel(); // Exercise Pending without waiting out the unchanged 12-second bound.
                return Task.FromResult(new PackageWorkerExecutionObservation(old, PackageWorkerExecutionState.CleanupPending));
            };
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.ReconcileAsync(request.Id!, gateway, cancellation.Token));
            Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
            Assert.Equal(reads, runtime.Reads);
            Assert.Equal(cancels, runtime.Cancels);
            Assert.Equal(backendReads + 1, runtime.BackendIds);
            Assert.Equal(sameBackend ? 1 : 0, observations.CaptureAttempts);
        }
        Assert.Single(original.Backend.Starts); // Neither receiver-bound recovery path exposes/replays Start.
    }

    [Fact]
    public async Task FreshSameExecutionBridgeDoesNotTurnMissingJobIntoTerminalOrCleanupProof()
    {
        var storage = new BuilderMemoryJournalStorage();
        await using var original = new ObservedBuilderFixture(BuilderTestJournal.Create(storage));
        var request = await original.MakeUncertainAsync(BuilderUiFixture.Project("fresh-missing-job"));
        var reference = Assert.Single(original.Observations.Captured).Reference;
        original.Backend.BeforeGet = (_, _) => Task.FromException(new SunderRpcException(new(SunderRpcErrorKind.Domain,
            "agent.builder.job-not-found", "The old result is absent from the bounded cache.")));
        original.Backend.BeforeCancel = null;
        original.Catalog.Rebind(refreshSessionGeneration: true);
        using var cancellation = new CancellationTokenSource();
        var observations = new BuilderReceiptObservations(reference.RuntimeInstanceId)
        {
            CaptureReference = _ => reference,
            Observe = (old, _) =>
            {
                cancellation.Cancel();
                return Task.FromResult(new PackageWorkerExecutionObservation(old, PackageWorkerExecutionState.CleanupPending));
            },
        };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BuilderTestJournal.Create(storage).ReconcileAsync(
            request.Id!, new BuilderBackendGateway(original.Catalog, observations), cancellation.Token));

        var pending = Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        Assert.Equal(JsonValueKind.Object, pending.GetProperty("recoveryBinding").ValueKind);
        Assert.Empty(ReadJournal(storage).GetProperty("completed").EnumerateArray());
        Assert.Single(original.Backend.Starts);
    }

    [Fact]
    public async Task LostPendingWriteAcknowledgementRetainsReservationWithoutAttemptingStart()
    {
        var storage = new BuilderMemoryJournalStorage();
        var journal = BuilderTestJournal.Create(storage);
        var backend = new JournalBackend();
        var execution = new JournalExecution();
        var admission = new BuilderJobAdmission(JournalRequest("write-ack", Path.Combine(Path.GetTempPath(), "write-ack")));
        var failure = new IOException("The durable write committed but its acknowledgement was lost.");
        storage.AfterWrite = (_, _) => Task.FromException(failure);

        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => journal.AdmitAndStartAsync(admission, backend,
            _ => Task.FromResult<IBuilderJobExecution>(execution))));

        Assert.True(admission.PendingWriteAttempted);
        Assert.False(admission.StartAttempted);
        Assert.Equal(0, execution.Starts);
        Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        storage.AfterWrite = null;
        var successor = BuilderTestJournal.Create(storage);
        await Assert.ThrowsAsync<IOException>(() => successor.ReconcileAsync(admission.Request.Id!, backend));
        await Assert.ThrowsAsync<InvalidOperationException>(() => successor.AdmitAndStartAsync(
            new(admission.Request), backend, _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution())));
        backend.State = PackageWorkerExecutionState.CleanupConfirmed;
        var stopped = await successor.ReconcileAsync(admission.Request.Id!, backend);
        Assert.Equal(BuilderJobStopOutcome.CleanupConfirmedUnknownOutcome, stopped.Outcome);
        Assert.Null(stopped.LastSnapshot); // No fabricated running/cancelled snapshot for a non-attempt.
        Assert.Equal(0, execution.Starts);
    }

    [Fact]
    public async Task OnlyTheDrainedOriginalAdmissionCanReconcileItsKnownNoStartAfterWriteAcknowledgementLoss()
    {
        var storage = new BuilderMemoryJournalStorage();
        var journal = BuilderTestJournal.Create(storage);
        var admission = new BuilderJobAdmission(JournalRequest("live-no-start-receipt", Path.GetTempPath()));
        var execution = new JournalExecution();
        var failure = new IOException("Original pending write acknowledgement was lost before Start.");
        storage.AfterWrite = (_, _) => Task.FromException(failure);
        await Assert.ThrowsAsync<IOException>(() => journal.AdmitAndStartAsync(admission, new JournalBackend(),
            _ => Task.FromResult<IBuilderJobExecution>(execution)));
        Assert.True(admission.BoundaryClosed);
        Assert.False(admission.StartAttempted);
        Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        storage.AfterWrite = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => BuilderTestJournal.Create(storage).AbortKnownNotStartedAsync(admission));

        var stopped = await journal.AbortKnownNotStartedAsync(admission);

        Assert.Equal(BuilderJobStopOutcome.NotStarted, stopped.Outcome);
        Assert.Equal(failure.Message, stopped.LastFailure);
        Assert.Null(stopped.TerminalSnapshot);
        Assert.Equal(0, execution.Starts);
        Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task PersistedExactNegativeTerminalSurvivesRecreationBeforeCompletionCompaction()
    {
        var storage = new BuilderMemoryJournalStorage();
        var journal = BuilderTestJournal.Create(storage);
        var backend = new JournalBackend();
        var admission = new BuilderJobAdmission(JournalRequest("negative-terminal", Path.GetTempPath()));
        await journal.AdmitAndStartAsync(admission, backend, _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution()));
        var failed = new BuilderJobSnapshot { Id = admission.Request.Id!, BackendId = admission.Request.BackendId,
            Operation = admission.Request.Operation, State = "failed", Message = "Build failed", Error = "Original compiler failure", Output = "partial diagnostics" };
        await journal.SavePartialAsync(admission.Reservation!, failed);

        var stopped = await BuilderTestJournal.Create(storage).ReconcileAsync(admission.Request.Id!, backend);

        Assert.Equal(BuilderJobStopOutcome.KnownTerminal, stopped.Outcome);
        Assert.Equal("failed", stopped.TerminalSnapshot?.State);
        Assert.Equal("Original compiler failure", stopped.TerminalSnapshot?.Error);
        Assert.Null(stopped.ExecutionObservation);
        Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OriginalTerminalReceiptSurvivesLostAcknowledgementAnd64CompletionsButCannotClearReusedJobIdentity(bool reusedJobIdentity)
    {
        var storage = new BuilderMemoryJournalStorage();
        var journal = BuilderTestJournal.Create(storage);
        var backend = new JournalBackend();
        var root = Path.Combine(Path.GetTempPath(), "builder-history-pressure", Guid.NewGuid().ToString("N"));
        var originalExecution = new JournalExecution();
        var admission = new BuilderJobAdmission(JournalRequest("lost-terminal-ack", Path.Combine(root, "old")));
        await journal.AdmitAndStartAsync(admission, backend, _ => Task.FromResult<IBuilderJobExecution>(originalExecution));
        var retainedPending = new BuilderJobAdmission(JournalRequest("independent-pending", Path.Combine(root, "still-owned")));
        await journal.AdmitAndStartAsync(retainedPending, backend, _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution()));
        var failed = new BuilderJobSnapshot { Id = admission.Request.Id!, Operation = admission.Request.Operation,
            BackendId = admission.Request.BackendId, State = "failed", Message = "Original build failed",
            Error = "Original compiler diagnostics", Output = "Partial original source diagnostics" };
        var terminal = BuilderJobStopResult.KnownTerminal(failed);
        storage.AfterWrite = (_, _) => Task.FromException(new IOException("Terminal write committed; acknowledgement was lost."));
        await Assert.ThrowsAsync<IOException>(() => journal.CompleteOwnedAsync(admission, terminal));
        Assert.NotNull(admission.TerminalReceipt);
        storage.AfterWrite = null;

        for (var index = 0; index < 64; index++)
        {
            var other = new BuilderJobAdmission(JournalRequest("history-pressure-" + index, Path.Combine(root, "other-" + index)));
            await journal.AdmitAndStartAsync(other, backend, _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution()));
            await journal.CompleteAsync(other.Reservation!, BuilderJobStopResult.KnownTerminal(new BuilderJobSnapshot
            { Id = other.Request.Id!, Operation = other.Request.Operation, BackendId = other.Request.BackendId, State = "succeeded", Message = "Done", Output = "" }));
        }
        Assert.Equal(64, ReadJournal(storage).GetProperty("completed").GetArrayLength());
        Assert.Null(await journal.ReadCompletedAsync(admission.Reservation!)); // Committed but locally unacknowledged history was compacted.
        await Assert.ThrowsAsync<InvalidDataException>(() => journal.CompleteAsync(admission.Reservation!, terminal));
        await Assert.ThrowsAsync<InvalidOperationException>(() => BuilderTestJournal.Create(storage).CompleteOwnedAsync(admission, terminal));

        if (reusedJobIdentity)
        {
            var replacement = new BuilderJobAdmission(JournalRequest(admission.Request.Id!, Path.Combine(root, "new-generation")));
            await journal.AdmitAndStartAsync(replacement, backend, _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution()));
            var before = storage.Bytes!.ToArray();

            await Assert.ThrowsAsync<InvalidDataException>(() => journal.CompleteOwnedAsync(admission, terminal));

            Assert.Equal(before, storage.Bytes);
            Assert.Contains(ReadJournal(storage).GetProperty("pending").EnumerateArray(),
                row => row.GetProperty("reservation").GetProperty("generation").GetGuid() == replacement.Reservation!.Generation);
        }
        else
        {
            var stopped = await journal.CompleteOwnedAsync(admission, terminal);

            Assert.Equal(BuilderJobStopOutcome.KnownTerminal, stopped.Outcome);
            Assert.Equal("failed", stopped.TerminalSnapshot?.State);
            Assert.Equal(failed.Error, stopped.TerminalSnapshot?.Error);
            Assert.Equal(failed.Output, stopped.TerminalSnapshot?.Output);
            Assert.Null(stopped.ExecutionObservation);
            Assert.Equal(64, ReadJournal(storage).GetProperty("completed").GetArrayLength());
        }
        Assert.Contains(ReadJournal(storage).GetProperty("pending").EnumerateArray(),
            row => row.GetProperty("reservation").GetProperty("generation").GetGuid() == retainedPending.Reservation!.Generation);
        Assert.Equal(1, originalExecution.Starts); // No replay and no old-job disappearance or timeout used as acknowledgement.
    }

    [Fact]
    public async Task LostNoStartCompletionAcknowledgementCanBeReadBackWithoutAnOldWorkerExit()
    {
        var storage = new BuilderMemoryJournalStorage();
        var writes = 0;
        storage.AfterWrite = (_, _) => Interlocked.Increment(ref writes) == 2
            ? Task.FromException(new IOException("Completion acknowledgement was lost.")) : Task.CompletedTask;
        var failure = new SunderRpcException(new(SunderRpcErrorKind.PermissionDenied, "test.denied", "Trusted pre-dispatch denial."));
        var execution = new JournalExecution { Start = (_, _) => Task.FromException<string>(failure), TrustedNoStart = error => ReferenceEquals(error, failure) };
        var journal = BuilderTestJournal.Create(storage);
        var admission = new BuilderJobAdmission(JournalRequest("lost-no-start-ack", Path.GetTempPath()));
        var combined = await Assert.ThrowsAsync<AggregateException>(() => journal.AdmitAndStartAsync(admission, new JournalBackend(),
            _ => Task.FromResult<IBuilderJobExecution>(execution)));
        Assert.Contains(failure, combined.InnerExceptions);
        Assert.Contains(combined.InnerExceptions, error => error is IOException);
        Assert.Null(admission.StopResult);
        storage.AfterWrite = null;

        var recovered = await BuilderTestJournal.Create(storage).ReadCompletedAsync(admission.Reservation!);

        Assert.Equal(BuilderJobStopOutcome.NotStarted, recovered?.Outcome);
        Assert.Equal(failure.Message, recovered?.LastFailure);
        Assert.Null(recovered?.TerminalSnapshot);
        Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        Assert.Equal(1, execution.Starts);
    }

    [Fact]
    public async Task CallerCancellationAfterPendingReadbackCanPersistPositiveNoStartWithoutPostRevocationPrivilege()
    {
        var storage = new BuilderMemoryJournalStorage();
        using var cancellation = new CancellationTokenSource();
        storage.AfterWrite = (_, _) => { cancellation.Cancel(); return Task.CompletedTask; };
        var journal = BuilderTestJournal.Create(storage);
        var execution = new JournalExecution();
        var admission = new BuilderJobAdmission(JournalRequest("cancel-before-dispatch", Path.GetTempPath()));

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.AdmitAndStartAsync(admission,
            new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(execution), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(0, execution.Starts);
        Assert.Equal(BuilderJobStopOutcome.NotStarted, admission.StopResult?.Outcome);
        Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        Assert.Equal(BuilderJobStopOutcome.NotStarted, (await BuilderTestJournal.Create(storage)
            .ReconcileAsync(admission.Request.Id!, new JournalBackend())).Outcome);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task InitialReadCancellationReachesAnAlreadyAcceptedCallbackAndDoesNotAbandonItsGate(bool admissionPath)
    {
        var storage = new BuilderMemoryJournalStorage();
        var entered = BuilderUiFixture.Signal();
        var observedCancellation = BuilderUiFixture.Signal();
        var releaseRead = BuilderUiFixture.Signal();
        storage.BeforeRead = async token =>
        {
            entered.TrySetResult();
            using var registration = token.Register(() => observedCancellation.TrySetResult());
            await releaseRead.Task; // Deliberately delay actual IO drain AFTER cancellation was requested.
            token.ThrowIfCancellationRequested();
        };
        var updates = new JournalStateUpdates(CancellationToken.None);
        var journal = new BuilderJobJournal(storage, updates);
        var admission = new BuilderJobAdmission(JournalRequest("bounded-initial-read", Path.GetTempPath()));
        var captures = 0;
        using var cancellation = new CancellationTokenSource();
        Task work = admissionPath
            ? journal.AdmitAndStartAsync(admission, new JournalBackend(), _ =>
                { captures++; return Task.FromResult<IBuilderJobExecution>(new JournalExecution()); }, cancellation.Token)
            : journal.ReconcileAsync(admission.Request.Id!, new JournalBackend(), cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await observedCancellation.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(work.IsCompleted);
            Assert.False(storage.GateAvailable);
            Assert.False(admission.BoundaryClosed);
            Assert.Equal(0, captures);
            Assert.Equal(0, storage.Writes);
        }
        finally { releaseRead.TrySetResult(); }

        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.True(storage.GateAvailable);
        Assert.Equal(admissionPath, admission.BoundaryClosed);
        Assert.Equal(1, updates.Accepted);
        Assert.Equal(0, captures);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task RevocationDuringPendingWriteNeverDispatchesOrRunsAFreshPrivilegedCleanupWrite()
    {
        var storage = new BuilderMemoryJournalStorage();
        using var revoked = new CancellationTokenSource();
        var updates = new JournalStateUpdates(revoked.Token);
        storage.AfterWrite = (_, _) => { revoked.Cancel(); return Task.CompletedTask; };
        var journal = new BuilderJobJournal(storage, updates);
        var execution = new JournalExecution();
        var admission = new BuilderJobAdmission(JournalRequest("revoked-before-dispatch", Path.GetTempPath()));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => journal.AdmitAndStartAsync(admission,
            new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(execution)));

        Assert.Equal(0, execution.Starts);
        Assert.Equal(1, storage.Writes);
        Assert.Equal(1, updates.Accepted);
        Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public async Task TrustedNotDispatchedCallerHandoffClearsReservationButNeverRetriesStartOrFabricatesTerminalState()
    {
        var storage = new BuilderMemoryJournalStorage();
        var journal = BuilderTestJournal.Create(storage);
        var failure = new SunderRpcException(new(SunderRpcErrorKind.StaleEndpoint, "rpc.caller-retired", "Committed caller handoff rejected this Start.",
            SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe));
        // This explicit test seam represents a trusted generated-boundary predicate,
        // not authority inferred from publicly constructible error flags.
        var execution = new JournalExecution { Start = (_, _) => Task.FromException<string>(failure), TrustedNoStart = error => ReferenceEquals(error, failure) };
        var admission = new BuilderJobAdmission(JournalRequest("handoff-no-start", Path.GetTempPath()));

        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => journal.AdmitAndStartAsync(admission,
            new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(execution))));

        Assert.Equal(1, execution.Starts);
        Assert.Equal(BuilderJobStopOutcome.NotStarted, admission.StopResult?.Outcome);
        Assert.Null(admission.StopResult?.TerminalSnapshot);
        Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        var later = new BuilderJobAdmission(JournalRequest("handoff-next-operation", Path.GetTempPath()));
        await BuilderTestJournal.Create(storage).AdmitAndStartAsync(later, new JournalBackend(),
            _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution()));
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.StaleEndpoint, SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe)]
    [InlineData(SunderRpcErrorKind.PermissionDenied, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)]
    [InlineData(SunderRpcErrorKind.Protocol, SunderRpcDispatchState.NotDispatched, SunderRpcRetrySafety.Safe)]
    public async Task UntrustedFlagsOrPossiblyDispatchedFailureCannotClearPendingStart(SunderRpcErrorKind kind,
        SunderRpcDispatchState dispatch, SunderRpcRetrySafety safety)
    {
        var storage = new BuilderMemoryJournalStorage();
        var failure = new SunderRpcException(new(kind, "test.untrusted", "Not positive no-start authority.", dispatch, safety));
        var execution = new JournalExecution { Start = (_, _) => Task.FromException<string>(failure) };
        var admission = new BuilderJobAdmission(JournalRequest("untrusted-start", Path.GetTempPath()));
        var journal = BuilderTestJournal.Create(storage);

        Assert.Same(failure, await Assert.ThrowsAsync<SunderRpcException>(() => journal.AdmitAndStartAsync(
            admission, new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(execution))));

        Assert.True(admission.StartAttempted);
        Assert.Null(admission.StopResult);
        Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
        Assert.Equal(1, execution.Starts);
        await Assert.ThrowsAsync<InvalidOperationException>(() => journal.AbortKnownNotStartedAsync(admission));
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{\"schema\":\"sunder.builder.admission\",\"version\":2,\"revision\":0,\"pending\":[],\"completed\":[]}")]
    [InlineData("{\"schema\":\"sunder.builder.admission\",\"version\":1,\"version\":1,\"revision\":0,\"pending\":[],\"completed\":[]}")]
    public async Task CorruptOrFutureJournalIsPreservedAndNeverReplacedByAnEmptyDocument(string persisted)
    {
        var storage = new BuilderMemoryJournalStorage { Bytes = Encoding.UTF8.GetBytes(persisted) };
        var captures = 0;
        await Assert.ThrowsAnyAsync<Exception>(() => BuilderTestJournal.Create(storage).AdmitAndStartAsync(
            new(JournalRequest("corrupt-journal", Path.GetTempPath())), new JournalBackend(), _ =>
            { captures++; return Task.FromResult<IBuilderJobExecution>(new JournalExecution()); }));
        Assert.Equal(persisted, Encoding.UTF8.GetString(storage.Bytes!));
        Assert.Equal(0, captures);
        Assert.Equal(0, storage.Writes);
    }

    [Fact]
    public async Task SourceOnlyOpeningNeverTouchesEvenACorruptJournalOrUnavailableObservationCapability()
    {
        var storage = new BuilderMemoryJournalStorage { Bytes = Encoding.UTF8.GetBytes("{broken") };
        await using var fixture = new ObservedBuilderFixture(BuilderTestJournal.Create(storage));
        var source = BuilderUiFixture.Project("cold-corrupt-journal");
        fixture.Backend.Projects[source.EntryPoint] = source;
        fixture.Observations.CaptureFailure = new NotSupportedException("Capture is unavailable.");

        Assert.Equal(source, await fixture.Application.InspectAsync(source.EntryPoint));
        Assert.Equal(source, Assert.Single(await fixture.Application.DiscoverAsync(source.ProjectFolder)));

        Assert.Equal(0, storage.Entries);
        Assert.Equal(0, fixture.Observations.CaptureAttempts);
        Assert.Empty(fixture.Observations.Observed);
    }

    [Fact]
    public async Task JournalCapacityAndByteBoundsRejectBeforeCaptureWithoutEvictingAnyPendingOwner()
    {
        var storage = new BuilderMemoryJournalStorage();
        var request = JournalRequest("capacity-seed", Path.Combine(Path.GetTempPath(), "capacity", "seed"));
        var execution = new JournalExecution();
        var reservations = Enumerable.Range(0, BuilderJobJournal.MaximumPending).Select(index =>
            new BuilderJournalPending(BuilderJobReservation.Create(request with
            { Id = "capacity-" + index, ProjectFolder = Path.Combine(Path.GetTempPath(), "capacity", index.ToString()) }, execution))).ToArray();
        storage.Bytes = JsonSerializer.SerializeToUtf8Bytes(new BuilderJournalDocument("sunder.builder.admission", 1, 1, reservations, []), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var before = storage.Bytes.ToArray();
        var captures = 0;
        await Assert.ThrowsAsync<IOException>(() => BuilderTestJournal.Create(storage).AdmitAndStartAsync(
            new(request with { Id = "capacity-new", ProjectFolder = Path.Combine(Path.GetTempPath(), "capacity", "independent") }), new JournalBackend(), _ =>
            { captures++; return Task.FromResult<IBuilderJobExecution>(execution); }));
        Assert.Equal(before, storage.Bytes);
        Assert.Equal(0, captures);
        storage.Bytes = new byte[BuilderJobJournal.MaximumDocumentBytes + 1];
        await Assert.ThrowsAsync<InvalidDataException>(() => BuilderTestJournal.Create(storage).AdmitAndStartAsync(
            new(request), new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(execution)));
        Assert.Equal(BuilderJobJournal.MaximumDocumentBytes + 1, storage.Bytes.Length);
    }

    [Fact]
    public async Task SdkGlobalReservationBlocksAnOtherwiseIndependentOtherTechnologyProject()
    {
        var storage = new BuilderMemoryJournalStorage();
        var journal = BuilderTestJournal.Create(storage);
        var request = new BuilderJobRequest { Id = "global-sdk", Operation = "repair-sdk", Kind = BuilderProjectKind.Node };
        await journal.AdmitAndStartAsync(new(request), new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(new JournalExecution()));
        var captures = 0;
        await Assert.ThrowsAsync<IOException>(() => BuilderTestJournal.Create(storage).AdmitAndStartAsync(
            new(JournalRequest("independent-dotnet", Path.Combine(Path.GetTempPath(), "different-project"))), new JournalBackend(), _ =>
            { captures++; return Task.FromResult<IBuilderJobExecution>(new JournalExecution()); }));
        Assert.Equal(0, captures);
        Assert.Single(ReadJournal(storage).GetProperty("pending").EnumerateArray());
    }

    [Fact]
    public void JournalRootComparisonIsConservativeForMacOsAndWindowsCaseAliases()
    {
        var root = Path.Combine(Path.GetTempPath(), "BuilderJournalCase", "not-created");
        var first = BuilderJobExclusionScope.Create(JournalRequest("case-first", root));
        var second = BuilderJobExclusionScope.Create(JournalRequest("case-second", root.ToUpperInvariant()));
        Assert.Equal(OperatingSystem.IsMacOS() || OperatingSystem.IsWindows(), first.Overlaps(second));
    }

    [FileSystemFact(TestFileSystemCapability.DirectorySymbolicLinks, "Builder journal physical exclusion requires directory symbolic links.")]
    public void NonexistentCreationRootUsesItsExistingPhysicalParentRatherThanTheSymlinkSpelling()
    {
        var root = Path.Combine(Path.GetTempPath(), "builder-journal-physical", Guid.NewGuid().ToString("N"));
        var physical = Path.Combine(root, "physical");
        Directory.CreateDirectory(physical);
        try
        {
            Directory.CreateSymbolicLink(Path.Combine(root, "alias"), physical);
            var first = BuilderJobExclusionScope.Create(JournalRequest("physical-first", Path.Combine(physical, "future", "child")));
            var alias = BuilderJobExclusionScope.Create(JournalRequest("physical-alias", Path.Combine(root, "alias", "future")));
            Assert.True(first.Overlaps(alias));
            Assert.Equal(new BuilderPathService().CanonicalEntryPoint(Path.Combine(physical, "future")), alias.PhysicalProjectRoot);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [FileSystemFact(TestFileSystemCapability.DirectorySymbolicLinks, "Builder admission revalidates physical roots before dispatch.")]
    public async Task PhysicalRootChangedAfterPendingWriteFailsClosedWithPositiveLocalNoStart()
    {
        var root = Path.Combine(Path.GetTempPath(), "builder-journal-root-change", Guid.NewGuid().ToString("N"));
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        var alias = Path.Combine(root, "alias");
        Directory.CreateDirectory(first);
        Directory.CreateDirectory(second);
        try
        {
            Directory.CreateSymbolicLink(alias, first);
            var storage = new BuilderMemoryJournalStorage();
            var writes = 0;
            storage.AfterWrite = (_, _) =>
            {
                if (Interlocked.Increment(ref writes) == 1)
                {
                    Directory.Delete(alias);
                    Directory.CreateSymbolicLink(alias, second);
                }
                return Task.CompletedTask;
            };
            var execution = new JournalExecution();
            var admission = new BuilderJobAdmission(JournalRequest("physical-root-changed", alias));

            await Assert.ThrowsAsync<InvalidDataException>(() => BuilderTestJournal.Create(storage).AdmitAndStartAsync(admission,
                new JournalBackend(), _ => Task.FromResult<IBuilderJobExecution>(execution)));

            Assert.Equal(0, execution.Starts);
            Assert.Equal(BuilderJobStopOutcome.NotStarted, admission.StopResult?.Outcome);
            Assert.Empty(ReadJournal(storage).GetProperty("pending").EnumerateArray());
            Assert.Null(admission.StopResult?.TerminalSnapshot);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static BuilderJobRequest JournalRequest(string id, string root)
        => new() { Id = id, Operation = "build", Kind = BuilderProjectKind.Dotnet, ProjectFolder = root };
    private static JsonElement ReadJournal(BuilderMemoryJournalStorage storage)
    { using var document = JsonDocument.Parse(storage.Bytes!); return document.RootElement.Clone(); }

    private sealed class JournalBackend : IBuilderBackend
    {
        internal PackageWorkerExecutionState State;
        public Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderToolchainStatus> GetToolchainsAsync(BuilderProjectKind kind, bool checkUpdates, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<BuilderRecordedJobRecovery?> RecoverRecordedJobAsync(BuilderJobReservation reservation, CancellationToken cancellationToken = default)
            => Task.FromResult<BuilderRecordedJobRecovery?>(null); // Explicit Host-only test seam, never a production default.
        public Task<PackageWorkerExecutionObservation> ObserveExecutionAsync(PackageWorkerExecutionReference reference, CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageWorkerExecutionObservation(reference, State));
    }

    private sealed class JournalExecution : IBuilderJobExecution
    {
        public string BackendId { get; } = new('a', 32);
        public BuilderJobProviderIdentity ProviderIdentity { get; } = new(BuilderViewModel.BuilderPackageId, "2.0.0", "builder.runtime",
            "younics.sunder.package.maker.runtime", "1.0.0", new('a', 64), new('b', 64), Guid.NewGuid(), 1, 1);
        public PackageWorkerExecutionReference Reference { get; } = new(Guid.NewGuid(), Guid.NewGuid().ToString("N"));
        internal Func<BuilderJobRequest, CancellationToken, Task<string>>? Start;
        internal Func<Exception, bool>? TrustedNoStart;
        internal int Starts;
        public Task<string> StartJobAsync(BuilderJobRequest request, CancellationToken cancellationToken = default)
        { Starts++; return Start?.Invoke(request, cancellationToken) ?? Task.FromResult(request.Id!); }
        public bool IsKnownNotStarted(Exception error) => TrustedNoStart?.Invoke(error) == true;
        public Task<BuilderJobSnapshot> GetJobAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task CancelJobAsync(string jobId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<PackageWorkerExecutionObservation> ObserveCleanupAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new PackageWorkerExecutionObservation(Reference, PackageWorkerExecutionState.Unknown));
    }

    private sealed class JournalContext(string root) : IPackageContext, IPackageStorageContext, IPackageRoleLocalWorkspace
    {
        public string PackageId => BuilderViewModel.BuilderPackageId;
        public string Version => "2.0.0";
        public string ContentRootPath => root;
        public IPackageStorageContext Storage => this;
        public IPackageRoleLocalWorkspace RoleLocalWorkspace => this;
        public string WorkspaceRootPath => root;
        public string GetLocalPath(string relativePath) => Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        public IPackageSettings Settings => throw new NotSupportedException();
        public IPackageSecrets Secrets => throw new NotSupportedException();
        public IPackageLogging Logging => throw new NotSupportedException();
        public IPackageFileStore Files => throw new NotSupportedException();
        public IPackageKeyValueStore State => throw new NotSupportedException();
    }

    private sealed class JournalStateUpdates(CancellationToken revocation) : IPackageAppStateUpdates
    {
        internal int Accepted;
        public Task RunAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken cancellationToken = default)
        {
            revocation.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Accepted);
            return Task.Run(async () =>
            {
                // Intentionally do NOT forward the registration token after
                // acceptance. The journal must link caller/deadline explicitly;
                // this seam is not a claim about one particular Host version.
                await update(new(CancellationToken.None, revocation));
            });
        }
    }
}

internal static class BuilderTestJournal
{
    internal static BuilderJobJournal Create(BuilderMemoryJournalStorage? storage = null)
        => new(storage ?? new BuilderMemoryJournalStorage(), new BuilderUiStateUpdates());
}

// Explicit test storage only; production always uses the App-role workspace/file gate.
internal sealed class BuilderMemoryJournalStorage : IBuilderJobJournalStorage
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    // This explicit test adapter starts with a positively initialized empty
    // document. Null after initialization is missing data, never a fresh store.
    internal byte[]? Bytes = JsonSerializer.SerializeToUtf8Bytes(BuilderJournalDocument.Empty, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    internal int Entries;
    internal int Writes;
    internal bool GateAvailable => _gate.CurrentCount == 1;
    internal Func<CancellationToken, Task>? BeforeRead;
    internal Func<byte[], CancellationToken, Task>? AfterWrite;
    public async Task<IAsyncDisposable> EnterAsync(CancellationToken token)
    { Interlocked.Increment(ref Entries); await _gate.WaitAsync(token); return new Release(_gate); }
    public async Task<byte[]?> ReadAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (BeforeRead is { } before) await before(token);
        token.ThrowIfCancellationRequested();
        return Bytes?.ToArray();
    }
    public async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Bytes = bytes.ToArray();
        Interlocked.Increment(ref Writes);
        if (AfterWrite is { } after) await after(bytes, token);
    }
    private sealed class Release(SemaphoreSlim gate) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() { gate.Release(); return ValueTask.CompletedTask; }
    }
}
