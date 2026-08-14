using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Subagents.Runtime;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class CatalogBackedSubsessionRuntimeAdapter(AgentRpcCatalog catalog) :
    ISubsessionSessionReader,
    ISubsessionCheckpointReader,
    ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications,
    IDisposable
{
    private readonly object _gate = new();
    private SubsessionLocalRuntimeAdapter? _runtime;
    private Action<Guid>? _sessionChanged;
    private Action<Guid, AgentTurnRecord>? _turnChanged;
    private Action? _resnapshotRequired;
    private bool _disposed;

    public event Action<Guid>? SessionChanged
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _sessionChanged += value;
                if (_runtime is not null) _runtime.SessionChanged += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _sessionChanged -= value;
                if (_runtime is not null) _runtime.SessionChanged -= value;
            }
        }
    }

    public event Action<Guid, AgentTurnRecord>? TurnChanged
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _turnChanged += value;
                if (_runtime is not null) _runtime.TurnChanged += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _turnChanged -= value;
                if (_runtime is not null) _runtime.TurnChanged -= value;
            }
        }
    }

    public event Action? ResnapshotRequired
    {
        add
        {
            lock (_gate)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                _resnapshotRequired += value;
                if (_runtime is not null) _runtime.ResnapshotRequired += value;
            }
        }
        remove
        {
            lock (_gate)
            {
                _resnapshotRequired -= value;
                if (_runtime is not null) _runtime.ResnapshotRequired -= value;
            }
        }
    }

    public Task<SubsessionSessionCatalog> ListSessionsAsync(
        CancellationToken cancellationToken = default)
        => GetRuntime().ListSessionsAsync(cancellationToken);

    public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default)
        => GetRuntime().ListLatestCheckpointsAsync(cancellationToken);

    public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
        => GetRuntime().ListRecentTurnsAsync(sessionId, limit, cancellationToken);

    public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => GetRuntime().ListTurnsBeforeAsync(
            sessionId,
            beforeCreatedAtUtc,
            beforeTurnId,
            limit,
            cancellationToken);

    public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => GetRuntime().ListTurnsAfterAsync(
            sessionId,
            afterCreatedAtUtc,
            afterTurnId,
            limit,
            cancellationToken);

    public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default)
        => GetRuntime().LoadAroundTurnAsync(
            sessionId,
            turnId,
            turnCreatedAtUtc,
            itemId,
            beforeLimit,
            afterLimit,
            cancellationToken);

    public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => GetRuntime().LoadToolDetailAsync(request, cancellationToken);

    public SubsessionsViewModel CreateViewModel()
        => new(this, this, this, this);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _runtime?.Dispose();
            _runtime = null;
            _sessionChanged = null;
            _turnChanged = null;
            _resnapshotRequired = null;
        }
    }

    private SubsessionLocalRuntimeAdapter GetRuntime()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtime is not null) return _runtime;
            var reference = catalog.GetServiceReferences(AgentRpcServices.RuntimeCatalogs)
                .SingleOrDefault()
                ?? throw new InvalidOperationException("The Agent runtime catalog is unavailable.");
            _runtime = new SubsessionLocalRuntimeAdapter(reference);
            if (_sessionChanged is not null) _runtime.SessionChanged += _sessionChanged;
            if (_turnChanged is not null) _runtime.TurnChanged += _turnChanged;
            if (_resnapshotRequired is not null) _runtime.ResnapshotRequired += _resnapshotRequired;
            return _runtime;
        }
    }
}
