using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.package.agent.subagents.owner", ServiceId = "subagents-management", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "subagents.management")]
[SunderRpcDomainError("subagents.owner.operation-failed", Description = "The requested Subagents operation could not be completed.")]
internal sealed partial class ManagementService(ISubagentOwnerClient operations)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<OwnerContent> QueryAsync(OwnerContent request, SunderRpcInvocationContext context, CancellationToken token = default)
        => Invoke<SubagentQuery>(request, context, operations.QueryAsync, token);

    [SunderRpcMethod(Errors = ["subagents.owner.operation-failed"])]
    public ValueTask<OwnerContent> CommandAsync(OwnerContent request, SunderRpcInvocationContext context, CancellationToken token = default)
        => Invoke<SubagentCommand>(request, context, operations.CommandAsync, token);

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["subagents.owner.operation-failed"])]
    public async IAsyncEnumerable<OwnerContent> ChangesAsync(ChangeSubscription request, SunderRpcInvocationContext context, [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var change in operations.SubscribeChangesAsync(new(request.AfterRevision.Value), token).WithCancellation(token).ConfigureAwait(false))
        {
            // Serialize before the retirement boundary: business/serialization faults
            // must not be mistaken for a retired Host content authority.
            var bytes = JsonSerializer.SerializeToUtf8Bytes(change, Json);
            await using var stream = new MemoryStream(bytes, writable: false);
            SunderRpcContentReference content;
            try
            {
                content = await context.RegisterContentAsync(stream, Options("subagents-change.json", bytes.LongLength), token).ConfigureAwait(false);
            }
            catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.StaleEndpoint
                or SunderRpcErrorKind.Cancelled or SunderRpcErrorKind.Unavailable or SunderRpcErrorKind.DeadlineExceeded)
            {
                yield break;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested || context.CancellationToken.IsCancellationRequested)
            {
                yield break;
            }
            yield return new() { Content = new() { Value = content } };
        }
    }

    private static async ValueTask<OwnerContent> Invoke<T>(OwnerContent request, SunderRpcInvocationContext context,
        Func<T, CancellationToken, ValueTask<SubagentProjection>> invoke, CancellationToken token) where T : notnull
    {
        await using var input = await context.OpenContentAsync(request.Content.Value, token).ConfigureAwait(false);
        var value = await JsonSerializer.DeserializeAsync<T>(input, Json, token).ConfigureAwait(false)
            ?? throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "subagents.owner.operation-failed", "The Subagents owner request is invalid."));
        var result = await invoke(value, token).ConfigureAwait(false);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        await using var output = new MemoryStream(bytes, writable: false);
        var content = await context.RegisterContentAsync(output, Options("subagents-projection.json", bytes.LongLength), token).ConfigureAwait(false);
        return new() { Content = new() { Value = content } };
    }

    private static SunderRpcContentRegistrationOptions Options(string fileName, long length)
        => new("application/json", fileName, length, Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1);
}

[SunderRpcTransparent]
internal sealed class ContentReference { public required SunderRpcContentReference Value { get; init; } }
[SunderRpcTransparent, SunderRpcFacet(Minimum = "0")]
internal sealed class ChangeRevision { public required long Value { get; init; } }
internal sealed class OwnerContent { public required ContentReference Content { get; init; } }
internal sealed class ChangeSubscription { public required ChangeRevision AfterRevision { get; init; } }
