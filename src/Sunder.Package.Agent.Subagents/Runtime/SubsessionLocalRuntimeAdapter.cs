using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal sealed class SubsessionLocalRuntimeAdapter :
    ISubsessionSessionReader,
    ISubsessionCheckpointReader,
    ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications,
    IDisposable
{
    private readonly AgentRpcReference<IAgentRuntimeCatalog> _runtimeReference;
    private readonly SemaphoreSlim _readGate = new(1, 1);
    private readonly object _eventSync = new();
    private AgentRpcLease<IAgentRuntimeCatalog>? _eventLease;
    private CancellationTokenRegistration _eventRetirement;
    private Action<Guid>? _sessionChanged;
    private Action<Guid, AgentTurnRecord>? _turnChanged;
    private int _disposed;

    internal SubsessionLocalRuntimeAdapter(
        AgentRpcReference<IAgentRuntimeCatalog> runtimeReference)
    {
        _runtimeReference = runtimeReference;
    }

    public event Action<Guid>? SessionChanged
    {
        add
        {
            lock (_eventSync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                EnsureEventLease();
                _sessionChanged += value;
            }
        }
        remove => RemoveSessionHandler(value);
    }

    public event Action<Guid, AgentTurnRecord>? TurnChanged
    {
        add
        {
            lock (_eventSync)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
                EnsureEventLease();
                _turnChanged += value;
            }
        }
        remove => RemoveTurnHandler(value);
    }

    public event Action? ResnapshotRequired
    {
        add { }
        remove { }
    }

    public Task<SubsessionSessionCatalog> ListSessionsAsync(CancellationToken cancellationToken = default)
        => RunReadAsync(
            runtime => new SubsessionSessionCatalog(runtime.ListSessions(), runtime.ListProfiles()),
            cancellationToken);

    public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default)
        => RunReadAsync<IReadOnlyList<AgentRunCheckpointRecord>>(
            runtime => runtime.ListSessions()
                .Select(session => runtime.GetLatestCheckpoint(session.SessionId))
                .OfType<AgentRunCheckpointRecord>()
                .ToArray(),
            cancellationToken);

    public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
        => RunReadAsync(
            runtime => BuildTranscriptPage(
                ListRecentTranscriptHeaders(runtime, sessionId, Math.Clamp(limit, 1, 500) + 1),
                limit,
                SubagentQueryKind.RecentTurns),
            cancellationToken);

    public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => RunReadAsync(
            runtime => BuildTranscriptPage(
                ListTranscriptHeadersBefore(
                    runtime,
                    sessionId,
                    beforeCreatedAtUtc,
                    beforeTurnId,
                    Math.Clamp(limit, 1, 500) + 1),
                limit,
                SubagentQueryKind.TurnsBefore),
            cancellationToken);

    public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => RunReadAsync(
            runtime => BuildTranscriptPage(
                ListTranscriptHeadersAfter(
                    runtime,
                    sessionId,
                    afterCreatedAtUtc,
                    afterTurnId,
                    Math.Clamp(limit, 1, 500) + 1),
                limit,
                SubagentQueryKind.TurnsAfter),
            cancellationToken);

    public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default)
        => RunReadAsync(
            runtime => BuildAroundTurnPage(
                runtime,
                sessionId,
                turnId,
                turnCreatedAtUtc,
                itemId,
                beforeLimit,
                afterLimit),
            cancellationToken);

    public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => RunReadAsync(
            runtime => GetTranscriptToolDetail(runtime, request) is { } detail
                ? SubsessionAroundTurnPayload.FitToolDetail(detail)
                : null,
            cancellationToken);

    private async Task<T> RunReadAsync<T>(
        Func<IAgentRuntimeCatalog, T> read,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (!_runtimeReference.TryAcquire(out var lease))
        {
            throw new InvalidOperationException("The Agent runtime catalog is unavailable.");
        }
        using (lease)
        {
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                lease.RetirementToken);
            await _readGate.WaitAsync(invocation.Token).ConfigureAwait(false);
            try
            {
                return await Task.Run(() =>
                {
                    invocation.Token.ThrowIfCancellationRequested();
                    var result = read(lease.Service);
                    invocation.Token.ThrowIfCancellationRequested();
                    return result;
                }, invocation.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException exception) when (
                lease.RetirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    $"Agent runtime package '{lease.PackageId}' became unavailable while the callback was running.",
                    exception);
            }
            finally
            {
                _readGate.Release();
            }
        }
    }

    private void EnsureEventLease()
    {
        if (_eventLease is not null)
        {
            return;
        }
        if (!_runtimeReference.TryAcquire(out var lease))
        {
            throw new InvalidOperationException("The Agent runtime catalog is unavailable.");
        }

        _eventLease = lease;
        lease.Service.SessionChanged += ForwardSessionChanged;
        lease.Service.TurnChanged += ForwardTurnChanged;
        _eventRetirement = lease.RetirementToken.Register(
            static state => ((SubsessionLocalRuntimeAdapter)state!).RetireEventLease(),
            this);
        if (_eventLease is null)
        {
            _eventRetirement.Dispose();
            _eventRetirement = default;
        }
    }

    private void RemoveSessionHandler(Action<Guid>? handler)
    {
        CancellationTokenRegistration registration = default;
        lock (_eventSync)
        {
            _sessionChanged -= handler;
            if (_sessionChanged is null && _turnChanged is null)
            {
                registration = ReleaseEventLease();
            }
        }
        registration.Dispose();
    }

    private void RemoveTurnHandler(Action<Guid, AgentTurnRecord>? handler)
    {
        CancellationTokenRegistration registration = default;
        lock (_eventSync)
        {
            _turnChanged -= handler;
            if (_sessionChanged is null && _turnChanged is null)
            {
                registration = ReleaseEventLease();
            }
        }
        registration.Dispose();
    }

    private void ForwardSessionChanged(Guid sessionId) => _sessionChanged?.Invoke(sessionId);

    private void ForwardTurnChanged(Guid sessionId, AgentTurnRecord turn)
        => _turnChanged?.Invoke(sessionId, turn);

    private void RetireEventLease()
    {
        lock (_eventSync)
        {
            _ = ReleaseEventLease();
        }
    }

    private CancellationTokenRegistration ReleaseEventLease()
    {
        var lease = _eventLease;
        if (lease is null)
        {
            return default;
        }

        _eventLease = null;
        lease.Service.SessionChanged -= ForwardSessionChanged;
        lease.Service.TurnChanged -= ForwardTurnChanged;
        lease.Dispose();
        var registration = _eventRetirement;
        _eventRetirement = default;
        return registration;
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenRegistration registration;
        lock (_eventSync)
        {
            _sessionChanged = null;
            _turnChanged = null;
            registration = ReleaseEventLease();
        }
        registration.Dispose();
    }

    internal static SubsessionAroundTurnPage BuildAroundTurnPage(
        IAgentRuntimeCatalog runtime,
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit)
    {
        var selectedSession = runtime.GetSession(sessionId)
            ?? throw new InvalidOperationException("The selected sub-session is no longer available.");
        if (selectedSession.ParentSessionId is null)
        {
            throw new InvalidOperationException("Subsession transcript navigation requires a child session.");
        }
        var predecessor = ListTranscriptHeadersBefore(runtime, sessionId, turnCreatedAtUtc, turnId, 1)
            .SingleOrDefault();
        var anchor = predecessor is null
            ? ListTranscriptHeadersAfter(
                    runtime,
                    sessionId,
                    turnCreatedAtUtc == DateTimeOffset.MinValue
                        ? turnCreatedAtUtc
                        : turnCreatedAtUtc.AddTicks(-1),
                    Guid.Empty,
                    1)
                .SingleOrDefault()
            : ListTranscriptHeadersAfter(
                    runtime,
                    sessionId,
                    predecessor.CreatedAtUtc,
                    predecessor.TurnId,
                    1)
                .SingleOrDefault();
        if (anchor?.TurnId != turnId)
        {
            throw new InvalidOperationException("The transcript anchor is no longer available.");
        }
        if (anchor.SessionId != sessionId)
        {
            throw new InvalidOperationException("The transcript anchor does not belong to the selected sub-session.");
        }
        var boundedBefore = Math.Clamp(beforeLimit, 0, 30);
        var boundedAfter = Math.Clamp(afterLimit, 0, 30);
        var before = ListTranscriptHeadersBefore(
            runtime,
            sessionId,
            anchor.CreatedAtUtc,
            turnId,
            boundedBefore + 1);
        var after = ListTranscriptHeadersAfter(
            runtime,
            sessionId,
            anchor.CreatedAtUtc,
            turnId,
            boundedAfter + 1);
        var hasOlder = before.Count > boundedBefore;
        var hasNewer = after.Count > boundedAfter;
        if (hasOlder)
        {
            before = before.Skip(before.Count - boundedBefore).ToArray();
        }
        if (hasNewer)
        {
            after = after.Take(boundedAfter).ToArray();
        }
        return SubsessionAroundTurnPayload.Fit(new SubsessionAroundTurnPage(
            [.. before, anchor, .. after],
            hasOlder,
            hasNewer,
            turnId), itemId);
    }

    internal static SubsessionTranscriptPage BuildTranscriptPage(
        IReadOnlyList<AgentTurnRecord> turns,
        int limit,
        SubagentQueryKind direction)
    {
        var boundedLimit = Math.Clamp(limit, 1, 500);
        var hasMore = turns.Count > boundedLimit;
        IReadOnlyList<AgentTurnRecord> pageTurns = !hasMore
            ? turns
            : direction is SubagentQueryKind.RecentTurns or SubagentQueryKind.TurnsBefore
                ? turns.Skip(turns.Count - boundedLimit).ToArray()
                : turns.Take(boundedLimit).ToArray();
        return SubsessionAroundTurnPayload.FitPage(
            new SubsessionTranscriptPage(pageTurns, hasMore),
            direction);
    }

    internal static IReadOnlyList<AgentTurnRecord> ListRecentTranscriptHeaders(
        IAgentRuntimeCatalog runtime,
        Guid sessionId,
        int limit)
        => runtime is IAgentTranscriptCatalog transcript
            ? transcript.ListRecentTranscriptHeaders(sessionId, limit)
            : runtime.ListRecentTurns(sessionId, limit);

    internal static IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersBefore(
        IAgentRuntimeCatalog runtime,
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit)
        => runtime is IAgentTranscriptCatalog transcript
            ? transcript.ListTranscriptHeadersBefore(
                sessionId,
                beforeCreatedAtUtc,
                beforeTurnId,
                limit)
            : runtime.ListTurnsBefore(sessionId, beforeCreatedAtUtc, beforeTurnId, limit);

    internal static IReadOnlyList<AgentTurnRecord> ListTranscriptHeadersAfter(
        IAgentRuntimeCatalog runtime,
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit)
        => runtime is IAgentTranscriptCatalog transcript
            ? transcript.ListTranscriptHeadersAfter(
                sessionId,
                afterCreatedAtUtc,
                afterTurnId,
                limit)
            : runtime.ListTurnsAfter(sessionId, afterCreatedAtUtc, afterTurnId, limit);

    internal static AgentTranscriptToolDetailRecord? GetTranscriptToolDetail(
        IAgentRuntimeCatalog runtime,
        AgentTranscriptToolDetailRequest request)
        => (runtime as IAgentTranscriptCatalog)?.GetTranscriptToolDetail(request);

}
