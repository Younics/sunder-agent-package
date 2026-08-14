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
        var channel = Channel.CreateBounded<MemoryChanged>(new BoundedChannelOptions(16)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        void Session(Guid id) => channel.Writer.TryWrite(new(MemoryChangeKind.Session, id));
        void Worker() => channel.Writer.TryWrite(new(MemoryChangeKind.Worker));
        inspector.SessionChanged += Session;
        inspector.SemanticWorkerStatusChanged += Worker;
        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return change;
            }
        }
        finally
        {
            inspector.SessionChanged -= Session;
            inspector.SemanticWorkerStatusChanged -= Worker;
            channel.Writer.TryComplete();
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
