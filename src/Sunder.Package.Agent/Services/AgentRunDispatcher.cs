using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

internal sealed class AgentRunDispatcher : IPackageBackgroundService, IAsyncDisposable
{
    private const int MaximumConcurrency = 4;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(250);

    private readonly AgentSessionService _sessionService;
    private readonly AgentRunPreparationService _preparationService;
    private readonly AgentRunStartService _startService;
    private readonly AgentRunExecutionService _executionService;
    private readonly AgentActiveRunRegistry _activeRunRegistry;
    private readonly AgentSessionTransitionGate _transitionGate;
    private readonly AgentSessionDeletionFence _deletionFence;
    private readonly object _syncRoot = new();
    private readonly SemaphoreSlim _wakeSignal = new(0, 1);
    private readonly SemaphoreSlim _concurrency = new(MaximumConcurrency, MaximumConcurrency);
    private readonly HashSet<Guid> _dispatchingRuns = [];
    private readonly HashSet<Guid> _dispatchingSessions = [];
    private readonly HashSet<Task> _activeTasks = [];

    private CancellationTokenSource? _lifetime;
    private Task? _worker;
    private bool _disposed;

    public AgentRunDispatcher(
        AgentSessionService sessionService,
        AgentRunPreparationService preparationService,
        AgentRunStartService startService,
        AgentRunExecutionService executionService,
        AgentActiveRunRegistry activeRunRegistry,
        AgentSessionTransitionGate? transitionGate = null,
        AgentSessionDeletionFence? deletionFence = null)
    {
        _sessionService = sessionService;
        _preparationService = preparationService;
        _startService = startService;
        _executionService = executionService;
        _activeRunRegistry = activeRunRegistry;
        _transitionGate = transitionGate ?? AgentSessionTransitionGate.Shared;
        _deletionFence = deletionFence ?? AgentSessionDeletionFence.Shared;
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_worker is not null)
            {
                return Task.CompletedTask;
            }
            _lifetime = new CancellationTokenSource();
            _worker = RunAsync(_lifetime.Token);
        }
        Signal();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task? worker;
        CancellationTokenSource? lifetime;
        lock (_syncRoot)
        {
            worker = _worker;
            lifetime = _lifetime;
            if (worker is null || lifetime is null)
            {
                return;
            }
            _worker = null;
            _lifetime = null;
        }

        await lifetime.CancelAsync().ConfigureAwait(false);
        Signal();
        try
        {
            await worker.WaitAsync(cancellationToken).ConfigureAwait(false);
            Task[] active;
            lock (_syncRoot)
            {
                active = _activeTasks.ToArray();
            }
            await Task.WhenAll(active).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested
                                                  && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            lifetime.Dispose();
        }
    }

    internal Task SignalStopAsync()
        => AgentRuntimeWorkerCancellation.SignalAsync(Volatile.Read(ref _lifetime));

    internal void Signal()
    {
        try
        {
            if (_wakeSignal.CurrentCount == 0)
            {
                _wakeSignal.Release();
            }
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SemaphoreFullException)
        {
        }
    }

    internal async Task<AgentRunCheckpointRecord> DispatchAndWaitAsync(
        Guid runId,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var run = _sessionService.GetRun(runId)
                ?? throw new InvalidOperationException($"Admitted run '{runId}' was not found.");
            if (run.Status != AgentDurableRunStatus.Preparing)
            {
                return await WaitForCurrentDispatchAsync(run, cancellationToken).ConfigureAwait(false);
            }

            if (TryReserve(run))
            {
                var acquired = false;
                try
                {
                    await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
                    acquired = true;
                    return await DispatchCoreAsync(
                        run,
                        cancellationToken,
                        preservePreparingOnCancellation: false).ConfigureAwait(false);
                }
                finally
                {
                    if (acquired)
                    {
                        _concurrency.Release();
                    }
                    Release(run);
                }
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        lock (_syncRoot)
        {
            if (_disposed)
            {
                return;
            }
            _disposed = true;
        }
        _wakeSignal.Dispose();
        _concurrency.Dispose();
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await _wakeSignal.WaitAsync(PollInterval, cancellationToken).ConfigureAwait(false);
                DispatchAvailable(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
                // The durable Preparing scan retries isolated dispatcher failures.
            }
        }
    }

    private void DispatchAvailable(CancellationToken cancellationToken)
    {
        foreach (var run in _sessionService.Store.ListPreparingAdmissions(64))
        {
            if (_concurrency.CurrentCount == 0 || !TryReserve(run))
            {
                continue;
            }

            var task = DispatchOwnedAsync(run, cancellationToken);
            lock (_syncRoot)
            {
                _activeTasks.Add(task);
            }
            _ = ObserveCompletionAsync(task);
        }
    }

    private async Task DispatchOwnedAsync(
        AgentDurableRunRecord run,
        CancellationToken cancellationToken)
    {
        var acquired = false;
        try
        {
            await _concurrency.WaitAsync(cancellationToken).ConfigureAwait(false);
            acquired = true;
            await DispatchCoreAsync(
                run,
                cancellationToken,
                preservePreparingOnCancellation: true).ConfigureAwait(false);
        }
        finally
        {
            if (acquired)
            {
                _concurrency.Release();
            }
            Release(run);
        }
    }

    private async Task ObserveCompletionAsync(Task task)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // DispatchCore persists attributable failures; the scan retries surviving Preparing work.
        }
        finally
        {
            lock (_syncRoot)
            {
                _activeTasks.Remove(task);
            }
            Signal();
        }
    }

    private async Task<AgentRunCheckpointRecord> DispatchCoreAsync(
        AgentDurableRunRecord candidate,
        CancellationToken cancellationToken,
        bool preservePreparingOnCancellation)
    {
        var run = _sessionService.GetRun(candidate.Key.RunId) ?? candidate;
        if (run.Status != AgentDurableRunStatus.Preparing || run.FinishedAtUtc is not null)
        {
            return await WaitForCurrentDispatchAsync(run, cancellationToken).ConfigureAwait(false);
        }

        var session = _sessionService.GetSession(run.Key.SessionId);
        var userTurn = run.UserTurnId is { } userTurnId
            ? _sessionService.GetTurn(userTurnId)
            : null;
        if (session is null
            || userTurn is null
            || userTurn.SessionId != run.Key.SessionId
            || userTurn.Role != AgentMessageRole.User
            || userTurn.Kind != AgentTurnKind.Message
            || string.IsNullOrWhiteSpace(run.WorkspaceId))
        {
            return FailPreparingRun(run, "The durable admitted user-turn context is no longer available.");
        }

        IReadOnlyList<AgentStoredAttachment> attachments;
        try
        {
            attachments = ReadStoredAttachments(userTurn);
        }
        catch (InvalidDataException ex)
        {
            return FailPreparingRun(run, ex.Message);
        }
        using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var runHandle = new AgentActiveRunHandle(
            run.Key.RunId,
            run.Key.RunRevision,
            run.StartedAtUtc,
            run.ProfileId,
            run.UserMessage,
            runCancellation)
        {
            DurableLease = new AgentDurableRunLease(run),
        };

        AgentRunActivationResult activation;
        using (await _transitionGate.EnterAsync(run.Key.SessionId, cancellationToken).ConfigureAwait(false))
        {
            if (_deletionFence.IsFenced(session))
            {
                return FailPreparingRun(run, "The session is being deleted and cannot dispatch its queued run.");
            }
            activation = _activeRunRegistry.Activate(run.Key.SessionId, runHandle);
        }
        if (!activation.IsAccepted)
        {
            return await WaitForCurrentDispatchAsync(run, cancellationToken).ConfigureAwait(false);
        }
        try
        {
            activation.DisplacedRun?.CancellationTokenSource.Cancel();
        }
        catch
        {
            // The durable run ordering remains authoritative.
        }

        AgentRunPlan? preparedPlan = null;
        CancellationTokenRegistration providerRetirementRegistration = default;
        try
        {
            var preparation = await _preparationService.PrepareAsync(
                session,
                run,
                runHandle,
                run.ProfileId,
                run.WorkspaceId,
                attachments,
                run.RollbackAnchorTurnId,
                runCancellation.Token).ConfigureAwait(false);
            if (preparation is AgentRunPreparationFailed failure)
            {
                return _sessionService.TryTransitionRun(
                           runHandle.DurableLease!,
                           AgentRunStatus.Failed,
                           failure.Summary)?.Checkpoint
                       ?? LatestCheckpoint(run);
            }

            var plan = ((AgentRunPrepared)preparation).Plan;
            preparedPlan = plan;
            providerRetirementRegistration = plan.ProviderSelection.RetirementToken.Register(
                static state => ((CancellationTokenSource)state!).Cancel(),
                runCancellation);
            var start = await _startService.StartAdmittedAsync(
                plan,
                userTurn,
                runCancellation.Token).ConfigureAwait(false);
            return start switch
            {
                AgentRunStartInterrupted interrupted => interrupted.Checkpoint,
                AgentRunStarted started => await _executionService.ExecuteAsync(started).ConfigureAwait(false),
                _ => throw new InvalidOperationException("Unknown admitted run start result."),
            };
        }
        catch (OperationCanceledException) when (
            preservePreparingOnCancellation
            && _sessionService.GetRun(run.Key.RunId)?.Status == AgentDurableRunStatus.Preparing)
        {
            return LatestCheckpoint(run);
        }
        catch (OperationCanceledException) when (
            !cancellationToken.IsCancellationRequested
            && _sessionService.GetRun(run.Key.RunId) is
            { Status: AgentDurableRunStatus.Interrupted or AgentDurableRunStatus.Stopped } terminalRun)
        {
            return LatestCheckpoint(terminalRun);
        }
        catch (OperationCanceledException)
        {
            _sessionService.TryTransitionRun(
                runHandle.DurableLease!,
                AgentRunStatus.Interrupted,
                "Agent run was canceled before provider execution started.");
            throw;
        }
        catch (Exception ex)
        {
            _sessionService.TryTransitionRun(
                runHandle.DurableLease!,
                AgentRunStatus.Failed,
                ex.Message);
            throw;
        }
        finally
        {
            providerRetirementRegistration.Dispose();
            preparedPlan?.Dispose();
            _activeRunRegistry.Complete(run.Key.SessionId, run.Key.RunId, run.Key.RunRevision);
        }
    }

    private async Task<AgentRunCheckpointRecord> WaitForCurrentDispatchAsync(
        AgentDurableRunRecord run,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            var current = _sessionService.GetRun(run.Key.RunId) ?? run;
            if (current.Status is not (AgentDurableRunStatus.Preparing or AgentDurableRunStatus.Running))
            {
                return LatestCheckpoint(current);
            }

            if (_activeRunRegistry.GetCurrent(
                    current.Key.SessionId,
                    current.Key.RunId,
                    current.Key.RunRevision) is { } active)
            {
                await active.Completion.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private AgentRunCheckpointRecord FailPreparingRun(AgentDurableRunRecord run, string summary)
        => _sessionService.TryTransitionRun(
               new AgentDurableRunLease(run),
               AgentRunStatus.Failed,
               summary)?.Checkpoint
           ?? LatestCheckpoint(run);

    private AgentRunCheckpointRecord LatestCheckpoint(AgentDurableRunRecord run)
        => _sessionService.GetLatestCheckpoint(run.Key.SessionId, run.Key.RunRevision)
           ?? throw new InvalidOperationException($"Run '{run.Key.RunId}' has no durable checkpoint.");

    private static IReadOnlyList<AgentStoredAttachment> ReadStoredAttachments(AgentTurnRecord userTurn)
    {
        var attachments = new List<AgentStoredAttachment>();
        foreach (var item in userTurn.Items.Where(item => item.Kind == AgentTurnItemKind.Attachment))
        {
            if (string.IsNullOrWhiteSpace(item.StructuredPayloadJson))
            {
                throw new InvalidDataException("Admitted attachment metadata is missing.");
            }
            AgentAttachmentMetadata metadata;
            try
            {
                metadata = JsonSerializer.Deserialize<AgentAttachmentMetadata>(item.StructuredPayloadJson)
                    ?? throw new InvalidDataException("Admitted attachment metadata is invalid.");
            }
            catch (JsonException ex)
            {
                throw new InvalidDataException("Admitted attachment metadata is invalid.", ex);
            }
            attachments.Add(new AgentStoredAttachment(metadata, item.TextContent));
        }
        return attachments;
    }

    private bool TryReserve(AgentDurableRunRecord run)
    {
        lock (_syncRoot)
        {
            if (_dispatchingRuns.Contains(run.Key.RunId)
                || _dispatchingSessions.Contains(run.Key.SessionId))
            {
                return false;
            }
            _dispatchingRuns.Add(run.Key.RunId);
            _dispatchingSessions.Add(run.Key.SessionId);
            return true;
        }
    }

    private void Release(AgentDurableRunRecord run)
    {
        lock (_syncRoot)
        {
            _dispatchingRuns.Remove(run.Key.RunId);
            _dispatchingSessions.Remove(run.Key.SessionId);
        }
    }
}
