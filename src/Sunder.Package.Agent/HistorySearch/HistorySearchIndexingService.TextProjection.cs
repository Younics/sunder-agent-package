namespace Sunder.Package.Agent.HistorySearch;

internal sealed partial class HistorySearchIndexingService
{
    private async Task RebuildTextProjectionAsync(CancellationToken cancellationToken)
    {
        long? generationId = null;
        var completedSessions = 0;
        try
        {
            generationId = _projection.BeginTextGeneration();
            _state.Publish(status => status with
            {
                Availability = HistorySearchAvailability.Rebuilding,
                ProgressCompleted = 0,
                ProgressTotal = null,
                FailureCode = null,
                FailureMessage = null,
            });
            string? sessionContinuation = null;
            long? sessionHighWaterMark = null;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = _authoritative.ListHistorySourceSessionsPage(
                    sessionContinuation,
                    HistorySearchLimits.AuthoritativePageSize,
                    sessionHighWaterMark);
                sessionHighWaterMark = page.InsertionHighWaterMark;
                foreach (var session in page.Sessions)
                {
                    await IndexCompleteSessionAsync(generationId.Value, session, cancellationToken).ConfigureAwait(false);
                    lock (_pendingLock)
                    {
                        _knownSessions[session.SessionId] = session;
                    }
                    completedSessions++;
                    _state.Publish(status => status with { ProgressCompleted = completedSessions });
                }
                sessionContinuation = page.Continuation;
            } while (sessionContinuation is not null);

            await ReplayPendingChangesIntoGenerationAsync(generationId.Value, cancellationToken).ConfigureAwait(false);
            _projection.ActivateTextGeneration(generationId.Value);
            _projection.MarkReconciled(UtcNow);
            ResetTextRebuildRetry();
            _state.PublishProjectionChanged(status => status with
            {
                Availability = HistorySearchAvailability.Ready,
                PendingChanges = PendingCount(),
                ProgressCompleted = completedSessions,
                ProgressTotal = completedSessions,
                FailureCode = null,
                FailureMessage = null,
            });
            if (_projection.GetConfiguration().SemanticEnabled)
            {
                lock (_pendingLock)
                {
                    _embeddingRebuildRequested = true;
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (generationId is { } startedGenerationId)
            {
                _projection.FailGeneration(startedGenerationId, "cancelled");
            }
            throw;
        }
        catch
        {
            try
            {
                if (generationId is { } startedGenerationId)
                {
                    _projection.FailGeneration(startedGenerationId, "text-rebuild-failed");
                }
            }
            catch
            {
                // Cleanup is best effort; retry must remain armed even if staging rows cannot be reclaimed yet.
            }
            ScheduleTextRebuildRetry();
            var searchable = _projection.TryPinActiveProjection() is not null;
            _state.Publish(status => status with
            {
                Availability = searchable
                    ? HistorySearchAvailability.Ready
                    : HistorySearchAvailability.Unavailable,
                FailureCode = "text-rebuild-failed",
                FailureMessage = searchable
                    ? "History indexing could not update. The current local index remains available and retry is automatic."
                    : "History indexing could not complete. Runtime will retry automatically after a cooldown.",
            });
        }
    }

    private async Task ProcessPendingChangesAsync(CancellationToken cancellationToken)
    {
        var snapshot = _projection.GetSnapshot();
        if (snapshot.ActiveTextGenerationId is not { } generationId)
        {
            if (PendingCount() > 0 && !_manuallyCleared)
            {
                RequestRebuildUnlessCoolingDown();
            }
            return;
        }

        var now = UtcNow;
        Guid[] sessions;
        Guid[] turns;
        lock (_pendingLock)
        {
            sessions = _pendingSessions.Where(pair => pair.Value <= now).Select(static pair => pair.Key).ToArray();
            turns = _pendingTurns.Where(pair => pair.Value <= now).Select(static pair => pair.Key).ToArray();
            foreach (var sessionId in sessions) _pendingSessions.Remove(sessionId);
            foreach (var turnId in turns) _pendingTurns.Remove(turnId);
        }

        var projectionChanged = false;
        foreach (var sessionId in sessions)
        {
            projectionChanged |= await ReindexSessionAsync(generationId, sessionId, cancellationToken)
                .ConfigureAwait(false);
        }
        var sessionSet = sessions.ToHashSet();
        var validations = _authoritative.GetHistorySearchValidations(turns);
        foreach (var turnId in turns)
        {
            var validation = validations.GetValueOrDefault(turnId);
            if (validation is not null && sessionSet.Contains(validation.SessionId))
            {
                continue;
            }
            projectionChanged |= await ReindexTurnAsync(generationId, turnId, validation, cancellationToken)
                .ConfigureAwait(false);
        }
        if (projectionChanged)
        {
            _state.PublishProjectionChanged(status => status with { PendingChanges = PendingCount() });
        }
        else
        {
            PublishPendingCount();
        }
    }

    private async Task ReconcileFullActiveGenerationAsync(CancellationToken cancellationToken)
    {
        if (_projection.GetSnapshot().ActiveTextGenerationId is not { } generationId)
        {
            return;
        }
        var authoritativeSessionIds = new HashSet<Guid>();
        var projectionChanged = false;
        string? continuation = null;
        long? sessionHighWaterMark = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _authoritative.ListHistorySourceSessionsPage(
                continuation,
                HistorySearchLimits.AuthoritativePageSize,
                sessionHighWaterMark);
            sessionHighWaterMark = page.InsertionHighWaterMark;
            foreach (var session in page.Sessions)
            {
                authoritativeSessionIds.Add(session.SessionId);
                projectionChanged |= await IndexCompleteSessionAsync(generationId, session, cancellationToken)
                    .ConfigureAwait(false);
                lock (_pendingLock)
                {
                    _knownSessions[session.SessionId] = session;
                }
            }
            continuation = page.Continuation;
        } while (continuation is not null);

        foreach (var indexedSessionId in _projection.ListIndexedSessionIds(generationId))
        {
            if (!authoritativeSessionIds.Contains(indexedSessionId))
            {
                projectionChanged |= _projection.DeleteSessionDocuments(indexedSessionId);
                if (_projection.GetSnapshot().ActiveTextGenerationId is null)
                {
                    RequestRebuild();
                    return;
                }
            }
        }
        var reconciledAt = UtcNow;
        _projection.MarkReconciled(reconciledAt);
        _nextReconciliation = reconciledAt + ReconciliationInterval;
        var update = (HistorySearchStatus status) => status with
        {
            Availability = HistorySearchAvailability.Ready,
            FailureCode = null,
            FailureMessage = null,
        };
        if (projectionChanged)
        {
            _state.PublishProjectionChanged(update);
        }
        else
        {
            _state.Publish(update);
        }
    }

    private async Task ReconcileChangedSliceAsync(CancellationToken cancellationToken)
    {
        if (_projection.GetSnapshot().ActiveTextGenerationId is not { } generationId)
        {
            return;
        }
        if (_periodicUpperWatermark is null)
        {
            _periodicLowerWatermark = _projection.GetSnapshot().LastReconciledAtUtc ?? DateTimeOffset.MinValue;
            _periodicUpperWatermark = UtcNow;
            _periodicContinuationAt = null;
            _periodicContinuationId = null;
            _periodicProjectionChanged = false;
        }
        var page = _authoritative.ListHistorySourceSessionsUpdatedPage(
            _periodicLowerWatermark!.Value,
            _periodicUpperWatermark.Value,
            _periodicContinuationAt,
            _periodicContinuationId,
            HistorySearchLimits.AuthoritativePageSize);
        foreach (var session in page.Sessions)
        {
            _periodicProjectionChanged |= await IndexCompleteSessionAsync(
                    generationId,
                    session,
                    cancellationToken)
                .ConfigureAwait(false);
            lock (_pendingLock)
            {
                _knownSessions[session.SessionId] = session;
            }
        }
        if (page.ContinuationSessionId is { } continuationId)
        {
            _periodicContinuationAt = page.ContinuationUpdatedAtUtc;
            _periodicContinuationId = continuationId;
            Signal();
            return;
        }
        _projection.MarkReconciled(_periodicUpperWatermark.Value);
        _periodicLowerWatermark = null;
        _periodicUpperWatermark = null;
        _periodicContinuationAt = null;
        _periodicContinuationId = null;
        _nextReconciliation = UtcNow + ReconciliationInterval;
        if (_periodicProjectionChanged)
        {
            _state.RefreshProjection();
        }
        else
        {
            _state.Refresh();
        }
        _periodicProjectionChanged = false;
    }

    private async Task ReplayPendingChangesIntoGenerationAsync(long generationId, CancellationToken cancellationToken)
    {
        Guid[] sessions;
        Guid[] turns;
        lock (_pendingLock)
        {
            sessions = _pendingSessions.Keys.ToArray();
            turns = _pendingTurns.Keys.ToArray();
            _pendingSessions.Clear();
            _pendingTurns.Clear();
        }
        foreach (var sessionId in sessions)
        {
            await ReindexSessionAsync(generationId, sessionId, cancellationToken).ConfigureAwait(false);
        }
        var sessionSet = sessions.ToHashSet();
        var validations = _authoritative.GetHistorySearchValidations(turns);
        foreach (var turnId in turns)
        {
            var validation = validations.GetValueOrDefault(turnId);
            if (validation is not null && sessionSet.Contains(validation.SessionId)) continue;
            await ReindexTurnAsync(generationId, turnId, validation, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<bool> ReindexSessionAsync(long generationId, Guid sessionId, CancellationToken cancellationToken)
    {
        var session = _authoritative.GetHistorySourceSession(sessionId);
        if (session is null)
        {
            var changed = _projection.DeleteSessionDocuments(sessionId);
            lock (_pendingLock) _knownSessions.Remove(sessionId);
            if (changed && _projection.GetSnapshot().ActiveTextGenerationId is null)
            {
                RequestRebuild();
            }
            return changed;
        }
        var projectionChanged = await IndexCompleteSessionAsync(generationId, session, cancellationToken)
            .ConfigureAwait(false);
        lock (_pendingLock) _knownSessions[sessionId] = session;
        return projectionChanged;
    }

    private async Task<bool> IndexCompleteSessionAsync(
        long generationId,
        HistorySourceSession session,
        CancellationToken cancellationToken)
    {
        var projectionChanged = false;
        var staleTurnIds = _projection.ListIndexedTurnIds(generationId, session.SessionId).ToHashSet();
        DateTimeOffset? turnContinuationAt = null;
        Guid? turnContinuationId = null;
        do
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = _authoritative.ListHistorySourceTurnsPage(
                session.SessionId,
                turnContinuationAt,
                turnContinuationId,
                HistorySearchLimits.AuthoritativePageSize);
            foreach (var turn in page.Turns)
            {
                staleTurnIds.Remove(turn.TurnId);
                projectionChanged |= _projection.ReplaceTurnDocuments(
                    generationId,
                    session.SessionId,
                    turn.TurnId,
                    HistorySearchExtractor.Extract(session, turn));
            }
            turnContinuationAt = page.ContinuationCreatedAtUtc;
            turnContinuationId = page.ContinuationTurnId;
        } while (turnContinuationId is not null);
        foreach (var staleTurnId in staleTurnIds)
        {
            projectionChanged |= _projection.ReplaceTurnDocuments(
                generationId,
                session.SessionId,
                staleTurnId,
                []);
        }
        await Task.CompletedTask.ConfigureAwait(false);
        return projectionChanged;
    }

    private async Task<bool> ReindexTurnAsync(
        long generationId,
        Guid turnId,
        HistorySearchValidation? validation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (validation is null)
        {
            return false;
        }
        var session = _authoritative.GetHistorySourceSession(validation.SessionId);
        var turn = _authoritative.GetTurn(turnId);
        if (session is null || turn is null || turn.SessionId != session.SessionId)
        {
            return _projection.ReplaceTurnDocuments(generationId, validation.SessionId, turnId, []);
        }
        var projectionChanged = _projection.ReplaceTurnDocuments(
            generationId,
            session.SessionId,
            turnId,
            HistorySearchExtractor.Extract(session, turn));
        await Task.CompletedTask.ConfigureAwait(false);
        return projectionChanged;
    }
}
