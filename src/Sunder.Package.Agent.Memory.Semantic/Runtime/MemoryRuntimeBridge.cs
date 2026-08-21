using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

internal sealed class MemoryRuntimeHandler(MemoryInspectorService inspector) : IMemoryInspectorRpcService
{
    public async ValueTask<MemoryProjection> QueryAsync(
        SunderRpcInvocationContext context,
        MemoryQuery request,
        CancellationToken cancellationToken = default)
        => request.Kind switch
        {
            MemoryQueryKind.Sessions => new(Sessions: inspector.ListSessions()),
            MemoryQueryKind.SessionCheckpoint => new(Checkpoint: inspector.GetSessionContextCheckpoint(Require(request.SessionId))),
            MemoryQueryKind.WorkingSummary => new(WorkingSummary: inspector.GetWorkingSummary(Require(request.SessionId))),
            MemoryQueryKind.Memories => new(Memories: inspector.ListMemories(Require(request.SessionId), request.SearchText, request.IncludeInactive)),
            MemoryQueryKind.Details => Details(Require(request.MemoryId)),
            MemoryQueryKind.SemanticState => new(SemanticState: await inspector.GetSemanticSessionStateAsync(Require(request.SessionId), request.ProfileId, cancellationToken)),
            MemoryQueryKind.SemanticIndexStatus => new(IndexStatus: inspector.GetSemanticIndexStatus(request.Memory ?? throw new MemoryInspectorOperationException("Memory is required."), request.SemanticContext)),
            MemoryQueryKind.WorkerStatus => new(WorkerStatus: inspector.GetSemanticWorkerStatus()),
            MemoryQueryKind.Metrics => new(Metrics: inspector.GetMetricsSnapshot()),
            _ => throw new MemoryInspectorOperationException("Unknown memory query."),
        };

    public async ValueTask<MemoryProjection> CommandAsync(
        SunderRpcInvocationContext context,
        MemoryCommand request,
        CancellationToken cancellationToken = default)
        => request.Kind switch
        {
            MemoryCommandKind.Update => new(Memory: inspector.UpdateMemory(request.MemoryId, Require(request.Category), Require(request.Content), request.Note)),
            MemoryCommandKind.Pin => new(Memory: inspector.SetPinned(request.MemoryId, request.IsPinned)),
            MemoryCommandKind.Contest => new(Memory: inspector.ContestMemory(request.MemoryId)),
            MemoryCommandKind.Forget => new(Memory: inspector.ForgetMemory(request.MemoryId)),
            MemoryCommandKind.Supersede => new(Memory: inspector.SupersedeMemory(request.MemoryId)),
            MemoryCommandKind.Correct => new(Correction: inspector.CreateCorrectedMemory(request.MemoryId, Require(request.Category), Require(request.Content))),
            MemoryCommandKind.Reindex => new(ReindexResult: await inspector.ReindexSessionAsync(request.MemoryId, request.ProfileId, cancellationToken)),
            _ => throw new MemoryInspectorOperationException("Unknown memory command."),
        };

    public async IAsyncEnumerable<MemoryChanged> ChangesAsync(
        SunderRpcInvocationContext context,
        MemoryChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var changes = new MemoryChangeQueue();
        void Session(Guid id) => changes.PublishSession(id);
        void Worker() => changes.PublishWorker();
        void Reset() => changes.PublishReset();
        changes.PublishReset();
        inspector.SessionChanged += Session;
        inspector.SemanticWorkerStatusChanged += Worker;
        inspector.ResetRequired += Reset;
        try
        {
            await foreach (var change in changes.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            inspector.SessionChanged -= Session;
            inspector.SemanticWorkerStatusChanged -= Worker;
            inspector.ResetRequired -= Reset;
            changes.Complete();
        }
    }

    private MemoryProjection Details(Guid memoryId) => new(
        Evidence: inspector.ListEvidence(memoryId),
        SupersedingMemory: inspector.GetSupersedingMemory(memoryId),
        SupersededMemories: inspector.ListSupersededMemories(memoryId),
        CorrectionLineage: inspector.ListCorrectionLineage(memoryId));

    private static Guid Require(Guid? value) => value ?? throw new MemoryInspectorOperationException("An identifier is required.");

    private static string Require(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new MemoryInspectorOperationException("A value is required.")
            : value;
}

internal sealed class MemoryChangeQueue
{
    // Larger distinct-session bursts collapse to a full refresh instead of retaining unbounded detail.
    internal const int PendingSessionCapacity = 16;

    private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });
    private readonly object _gate = new();
    private readonly HashSet<Guid> _sessions = [];
    private bool _worker;
    private bool _reset;
    private bool _completed;

    public void PublishSession(Guid sessionId)
    {
        lock (_gate)
        {
            if (_completed || _reset || _sessions.Contains(sessionId))
            {
                return;
            }
            if (_sessions.Count == PendingSessionCapacity)
            {
                _sessions.Clear();
                _worker = false;
                _reset = true;
            }
            else
            {
                _sessions.Add(sessionId);
            }
        }
        _signals.Writer.TryWrite(true);
    }

    public void PublishWorker()
    {
        lock (_gate)
        {
            if (_completed || _reset || _worker)
            {
                return;
            }
            _worker = true;
        }
        _signals.Writer.TryWrite(true);
    }

    public void PublishReset()
    {
        lock (_gate)
        {
            if (_completed || _reset)
            {
                return;
            }
            _sessions.Clear();
            _worker = false;
            _reset = true;
        }
        _signals.Writer.TryWrite(true);
    }

    public async IAsyncEnumerable<MemoryChanged> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var _ in _signals.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            while (TakePending() is { Length: > 0 } pending)
            {
                foreach (var change in pending)
                {
                    yield return change;
                }
            }
        }
    }

    public void Complete()
    {
        lock (_gate)
        {
            _completed = true;
        }
        _signals.Writer.TryComplete();
    }

    private MemoryChanged[] TakePending()
    {
        lock (_gate)
        {
            if (_reset)
            {
                _reset = false;
                _sessions.Clear();
                _worker = false;
                return [new MemoryChanged(MemoryChangeKind.ResetRequired)];
            }

            if (_sessions.Count == 0 && !_worker)
            {
                return [];
            }

            var pending = new MemoryChanged[_sessions.Count + (_worker ? 1 : 0)];
            var index = 0;
            foreach (var sessionId in _sessions)
            {
                pending[index++] = new MemoryChanged(MemoryChangeKind.Session, sessionId);
            }
            if (_worker)
            {
                pending[index] = new MemoryChanged(MemoryChangeKind.Worker);
            }
            _sessions.Clear();
            _worker = false;
            return pending;
        }
    }
}
