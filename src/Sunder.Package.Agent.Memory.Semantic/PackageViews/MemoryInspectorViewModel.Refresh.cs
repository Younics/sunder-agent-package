using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Shared.Presentation;

namespace Sunder.Package.Agent.Memory.Semantic.PackageViews;

public sealed partial class MemoryInspectorViewModel
{
    private async Task InitializeCoreAsync(CancellationToken cancellationToken)
    {
        await _memoryInspectorService.InitializeAsync(cancellationToken).ConfigureAwait(false);
        var sessionsTask = _memoryInspectorService.ListSessionsAsync(cancellationToken);
        var workerStatusTask = _memoryInspectorService.GetSemanticWorkerStatusAsync(cancellationToken);
        var metricsTask = _memoryInspectorService.GetMetricsSnapshotAsync(cancellationToken);
        await Task.WhenAll(sessionsTask, workerStatusTask, metricsTask).ConfigureAwait(false);
        var initialState = new MemoryInspectorInitialState(
            await sessionsTask.ConfigureAwait(false),
            await workerStatusTask.ConfigureAwait(false),
            await metricsTask.ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();

        Task sessionLoad = Task.CompletedTask;
        var replaySessionRefresh = false;
        var replayWorkerRefresh = false;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            ApplySessions(initialState.Sessions, preferredSessionId: null);
            ApplySemanticWorkerStatus(initialState.WorkerStatus);
            ApplyMetricsSummary(initialState.Metrics);
            _isInitialized = true;
            replaySessionRefresh = _initializationSessionRefreshPending;
            replayWorkerRefresh = _initializationWorkerRefreshPending;
            _initializationSessionRefreshPending = false;
            _initializationWorkerRefreshPending = false;
            if (!replaySessionRefresh)
            {
                sessionLoad = StartSessionLoadAsync(SelectedSession, preferredMemoryId: null);
            }
        }).ConfigureAwait(false);
        if (replaySessionRefresh)
        {
            sessionLoad = StartSessionRefresh(preferredMemoryId: null, cancellationToken);
        }

        var workerRefresh = replayWorkerRefresh
            ? RefreshSemanticWorkerStatusAsync(cancellationToken)
            : Task.CompletedTask;
        await Task.WhenAll(sessionLoad, workerRefresh)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    private Task StartSessionRefresh(
        Guid? preferredMemoryId = null,
        CancellationToken cancellationToken = default)
    {
        _currentSessionRefresh = ReloadSessionsAsync(preferredMemoryId, cancellationToken);
        return _currentSessionRefresh;
    }

    private async Task ReloadSessionsAsync(
        Guid? preferredMemoryId,
        CancellationToken cancellationToken)
    {
        MemorySessionRefreshRequest? request = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }

            request = new MemorySessionRefreshRequest(
                _requests.Begin(SessionRefreshChannel, cancellationToken),
                SelectedSession?.SessionId,
                _sessionSelectionRevision,
                preferredMemoryId ?? SelectedMemory?.MemoryId,
                _memorySelectionRevision);
        }).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        try
        {
            var sessions = await _memoryInspectorService.ListSessionsAsync(request.Ticket.CancellationToken)
                .ConfigureAwait(false);
            Task sessionLoad = Task.CompletedTask;
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_requests.IsCurrent(request.Ticket))
                {
                    return;
                }

                var sessionSelectionChanged = request.SessionSelectionRevision != _sessionSelectionRevision;
                var selectedSessionId = sessionSelectionChanged
                    ? SelectedSession?.SessionId
                    : request.SelectedSessionId;
                if (sessionSelectionChanged
                    && selectedSessionId is not null
                    && !sessions.Any(session => session.SessionId == selectedSessionId))
                {
                    return;
                }

                var preferredSelection = sessionSelectionChanged
                                         || request.MemorySelectionRevision != _memorySelectionRevision
                    ? SelectedMemory?.MemoryId
                    : request.PreferredMemoryId;
                ApplySessions(sessions, selectedSessionId);
                sessionLoad = StartSessionLoadAsync(
                    SelectedSession,
                    preferredSelection,
                    request.Ticket);
            }).ConfigureAwait(false);
            await sessionLoad.WaitAsync(request.Ticket.CancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            request.Ticket.CancellationToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _requests.Complete(request.Ticket);
        }
    }

    private void ApplySessions(
        IReadOnlyList<AgentSessionRecord> sessions,
        Guid? preferredSessionId)
    {
        _suppressSessionSelectionHandlers = true;
        try
        {
            Sessions.Clear();
            foreach (var session in sessions)
            {
                Sessions.Add(session);
            }

            SelectedSession = Sessions.FirstOrDefault(session => session.SessionId == preferredSessionId)
                ?? Sessions.FirstOrDefault();
        }
        finally
        {
            _suppressSessionSelectionHandlers = false;
        }
    }

    private async Task RefreshSemanticWorkerStatusAsync(CancellationToken cancellationToken)
    {
        LatestRequestTicket? request = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }
            request = _requests.Begin(WorkerStatusRefreshChannel, cancellationToken);
        }).ConfigureAwait(false);
        if (request is null)
        {
            return;
        }

        try
        {
            var statusTask = _memoryInspectorService.GetSemanticWorkerStatusAsync(
                request.Value.CancellationToken);
            var metricsTask = _memoryInspectorService.GetMetricsSnapshotAsync(
                request.Value.CancellationToken);
            await Task.WhenAll(statusTask, metricsTask).ConfigureAwait(false);
            var status = await statusTask.ConfigureAwait(false);
            var metrics = await metricsTask.ConfigureAwait(false);
            await _uiDispatcher.InvokeAsync(() =>
            {
                if (_disposed || !_requests.IsCurrent(request.Value))
                {
                    return;
                }
                ApplySemanticWorkerStatus(status);
                ApplyMetricsSummary(metrics);
            }).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            request.Value.CancellationToken.IsCancellationRequested
            && !cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _requests.Complete(request.Value);
        }
    }

    private void ApplySemanticWorkerStatus(SemanticMemoryWorkerStatusRecord status)
    {
        SemanticWorkerStatusText = status.StatusText;
        HasSemanticWorkerFailure = status.HasFailure;
        OnPropertyChanged(nameof(HasSemanticWorkerStatus));
    }

    private void ApplyMetricsSummary(SemanticMemoryMetricsSnapshot metrics)
    {
        MemoryMetricsSummaryText =
            $"Promotions: {metrics.PromotionWriteCount}/{metrics.PromotionCandidateCount} candidates committed\n" +
            $"Recall: {metrics.RecallRequestCount} requests, {metrics.RecallEntryCount} total entries returned\n" +
            $"Corrections: {metrics.CorrectionCount}\n" +
            $"Worker failures: {metrics.WorkerFailureCount}";
        OnPropertyChanged(nameof(HasMemoryMetricsSummary));
    }

    private void OnSessionChanged(Guid sessionId)
        => _tasks.Run(cancellationToken => HandleSessionChangedAsync(sessionId, cancellationToken));

    private async Task HandleSessionChangedAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var refreshSelectedSession = false;
        Guid? selectedMemoryId = null;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (!_isInitialized)
            {
                _initializationSessionRefreshPending = true;
                return;
            }

            refreshSelectedSession = SelectedSession?.SessionId == sessionId;
            if (refreshSelectedSession)
            {
                selectedMemoryId = SelectedMemory?.MemoryId;
            }
        }).ConfigureAwait(false);
        if (refreshSelectedSession)
        {
            await StartSessionRefresh(selectedMemoryId, cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnSemanticWorkerStatusChanged()
        => _tasks.Run(HandleSemanticWorkerStatusChangedAsync);

    private async Task HandleSemanticWorkerStatusChangedAsync(CancellationToken cancellationToken)
    {
        var refresh = false;
        await _uiDispatcher.InvokeAsync(() =>
        {
            if (_disposed)
            {
                return;
            }
            if (!_isInitialized)
            {
                _initializationWorkerRefreshPending = true;
                return;
            }

            refresh = true;
        }).ConfigureAwait(false);
        if (refresh)
        {
            await RefreshSemanticWorkerStatusAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record MemoryInspectorInitialState(
        IReadOnlyList<AgentSessionRecord> Sessions,
        SemanticMemoryWorkerStatusRecord WorkerStatus,
        SemanticMemoryMetricsSnapshot Metrics);

    private sealed record MemorySessionRefreshRequest(
        LatestRequestTicket Ticket,
        Guid? SelectedSessionId,
        long SessionSelectionRevision,
        Guid? PreferredMemoryId,
        long MemorySelectionRevision);
}
