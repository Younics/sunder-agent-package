using System.Runtime.CompilerServices;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Memory.Semantic.Runtime;

namespace Sunder.Package.Agent.Memory.Semantic.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.agent.memory.semantic.inspector", ServiceId = "memory-inspector", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "semantic.memory.inspector")]
[SunderRpcDomainError("memory.semantic.operation-failed", Description = "The requested semantic memory operation could not be completed.")]
internal sealed partial class ManagementService(IMemoryInspectorRpcService operations)
{
    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public async ValueTask<MemoryProjectionContent> QueryAsync(MemoryQuery request, SunderRpcInvocationContext context, CancellationToken token = default)
    {
        try
        {
            var result = await operations.QueryAsync(context, MemoryValueMapper.Convert<Domain.MemoryQuery>(request), token).ConfigureAwait(false);
            return new() { Content = new() { Value = await ProjectionContent.Write(context, result, token).ConfigureAwait(false) } };
        }
        catch (MemoryInspectorOperationException error) { throw Failure(error); }
    }

    [SunderRpcMethod(Errors = ["memory.semantic.operation-failed"])]
    public async ValueTask<MemoryProjectionContent> CommandAsync(MemoryCommand request, SunderRpcInvocationContext context, CancellationToken token = default)
    {
        try
        {
            var result = await operations.CommandAsync(context, MemoryValueMapper.Convert<Domain.MemoryCommand>(request), token).ConfigureAwait(false);
            return new() { Content = new() { Value = await ProjectionContent.Write(context, result, token).ConfigureAwait(false) } };
        }
        catch (MemoryInspectorOperationException error) { throw Failure(error); }
    }

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["memory.semantic.operation-failed"])]
    public async IAsyncEnumerable<MemoryChanged> ChangesAsync(MemoryChangeSubscription request, SunderRpcInvocationContext context, [EnumeratorCancellation] CancellationToken token = default)
    {
        await using var changes = operations.ChangesAsync(context, new(), token).GetAsyncEnumerator(token);
        while (await MoveNext(changes).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return MemoryValueMapper.Convert<MemoryChanged>(changes.Current);
        }
    }

    private static async ValueTask<bool> MoveNext(IAsyncEnumerator<Domain.MemoryChanged> changes)
    {
        try { return await changes.MoveNextAsync().ConfigureAwait(false); }
        catch (MemoryInspectorOperationException error) { throw Failure(error); }
    }

    private static SunderRpcException Failure(MemoryInspectorOperationException error)
    {
        var message = string.IsNullOrWhiteSpace(error.Message) ? "The requested semantic memory operation failed." : error.Message;
        return new(new(SunderRpcErrorKind.Domain, "memory.semantic.operation-failed", message.Length > 2_048 ? message[..2_048] : message));
    }
}
