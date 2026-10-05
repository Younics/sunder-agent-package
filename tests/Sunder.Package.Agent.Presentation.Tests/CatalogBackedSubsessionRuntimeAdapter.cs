extern alias SubagentsApp;
extern alias SubagentsWorker;

using SubagentsApp::Sunder.Package.Agent.Contracts.Models;
using SubagentsApp::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Rpc;
using SubsessionLocalRuntimeAdapter = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime.SubsessionLocalRuntimeAdapter;
using WorkerTurn = SubagentsWorker::Sunder.Package.Agent.Contracts.Models.AgentTurnRecord;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Presentation.Tests;

internal sealed class CatalogBackedSubsessionRuntimeAdapter(ISunderRpcClient rpc) :
    ISubsessionSessionReader,
    ISubsessionCheckpointReader,
    ISubsessionTranscriptPageReader,
    ISubsessionChangeNotifications,
    IDisposable
{
    private readonly object _gate = new();
    private SubagentRuntimeBindings? _bindings;
    private SubsessionLocalRuntimeAdapter? _runtime;
    private Action<Guid>? _sessionChanged;
    private Action<Guid, AgentTurnRecord>? _turnChanged;
    private Action? _resnapshotRequired;
    private bool _disposed;

    internal CatalogBackedSubsessionRuntimeAdapter(RegressionTestExtensionCatalog catalog)
        : this(catalog.RpcForCaller("sunder.package.agent.subagents")) { }

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
                if (_runtime is not null && value is not null) _runtime.TurnChanged += AgentRoleBoundary.As<Action<Guid, WorkerTurn>>(value);
            }
        }
        remove
        {
            lock (_gate)
            {
                _turnChanged -= value;
                if (_runtime is not null && value is not null) _runtime.TurnChanged -= AgentRoleBoundary.As<Action<Guid, WorkerTurn>>(value);
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
        => AgentRoleBoundary.As<Task<SubsessionSessionCatalog>>(GetRuntime().ListSessionsAsync(cancellationToken));

    public Task<IReadOnlyList<AgentRunCheckpointRecord>> ListLatestCheckpointsAsync(
        CancellationToken cancellationToken = default)
        => AgentRoleBoundary.As<Task<IReadOnlyList<AgentRunCheckpointRecord>>>(GetRuntime().ListLatestCheckpointsAsync(cancellationToken));

    public Task<SubsessionTranscriptPage> ListRecentTurnsAsync(
        Guid sessionId,
        int limit,
        CancellationToken cancellationToken = default)
        => AgentRoleBoundary.As<Task<SubsessionTranscriptPage>>(GetRuntime().ListRecentTurnsAsync(sessionId, limit, cancellationToken));

    public Task<SubsessionTranscriptPage> ListTurnsBeforeAsync(
        Guid sessionId,
        DateTimeOffset beforeCreatedAtUtc,
        Guid beforeTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => AgentRoleBoundary.As<Task<SubsessionTranscriptPage>>(GetRuntime().ListTurnsBeforeAsync(
            sessionId,
            beforeCreatedAtUtc,
            beforeTurnId,
            limit,
            cancellationToken));

    public Task<SubsessionTranscriptPage> ListTurnsAfterAsync(
        Guid sessionId,
        DateTimeOffset afterCreatedAtUtc,
        Guid afterTurnId,
        int limit,
        CancellationToken cancellationToken = default)
        => AgentRoleBoundary.As<Task<SubsessionTranscriptPage>>(GetRuntime().ListTurnsAfterAsync(
            sessionId,
            afterCreatedAtUtc,
            afterTurnId,
            limit,
            cancellationToken));

    public Task<SubsessionAroundTurnPage> LoadAroundTurnAsync(
        Guid sessionId,
        Guid turnId,
        DateTimeOffset turnCreatedAtUtc,
        Guid itemId,
        int beforeLimit,
        int afterLimit,
        CancellationToken cancellationToken = default)
        => AgentRoleBoundary.As<Task<SubsessionAroundTurnPage>>(GetRuntime().LoadAroundTurnAsync(
            sessionId,
            turnId,
            turnCreatedAtUtc,
            itemId,
            beforeLimit,
            afterLimit,
            cancellationToken));

    public Task<AgentTranscriptToolDetailRecord?> LoadToolDetailAsync(
        AgentTranscriptToolDetailRequest request,
        CancellationToken cancellationToken = default)
        => AgentRoleBoundary.As<Task<AgentTranscriptToolDetailRecord?>>(GetRuntime().LoadToolDetailAsync(
            AgentRoleBoundary.As<SubagentsWorker::Sunder.Package.Agent.Contracts.Models.AgentTranscriptToolDetailRequest>(request), cancellationToken));

    public SubsessionsViewModel CreateViewModel()
        => new(this, this, this, this);

    public void Dispose()
    {
        SubagentRuntimeBindings? bindings;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _runtime?.Dispose();
            _runtime = null;
            bindings = _bindings;
            _bindings = null;
            _sessionChanged = null;
            _turnChanged = null;
            _resnapshotRequired = null;
        }
        if (bindings is not null)
            Task.Run(async () => await bindings.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
    }

    private SubsessionLocalRuntimeAdapter GetRuntime()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_runtime is not null) return _runtime;
            _bindings ??= new SubagentRuntimeBindings(rpc);
            _bindings.StartAsync().GetAwaiter().GetResult();
            var reference = _bindings.GetRuntimeCatalogs()
                .SingleOrDefault()
                ?? throw new InvalidOperationException("The Agent runtime catalog is unavailable.");
            _runtime = new SubsessionLocalRuntimeAdapter(reference);
            if (_sessionChanged is not null) _runtime.SessionChanged += _sessionChanged;
            if (_turnChanged is not null) _runtime.TurnChanged += AgentRoleBoundary.As<Action<Guid, WorkerTurn>>(_turnChanged);
            if (_resnapshotRequired is not null) _runtime.ResnapshotRequired += _resnapshotRequired;
            return _runtime;
        }
    }
}
