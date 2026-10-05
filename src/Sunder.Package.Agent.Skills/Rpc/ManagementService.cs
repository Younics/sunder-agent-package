using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Skills.Runtime;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Skills.Rpc;

[SunderRpcService(SunderRpcExposure.Package, ContractId = "sunder.package.agent.skills.owner", ServiceId = "skills-management", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "skills.management")]
[SunderRpcDomainError("skills.owner.operation-failed", Description = "The requested Skills operation could not be completed.")]
internal sealed partial class ManagementService(ISkillOwnerClient operations)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["skills.owner.operation-failed"])]
    public async ValueTask<OwnerContent> QueryAsync(OwnerEmpty request, SunderRpcInvocationContext context, CancellationToken token = default)
        => await Write(context, await operations.QueryAsync(token).ConfigureAwait(false), token).ConfigureAwait(false);

    [SunderRpcMethod(Errors = ["skills.owner.operation-failed"])]
    public async ValueTask<OwnerContent> CommandAsync(OwnerContent request, SunderRpcInvocationContext context, CancellationToken token = default)
    {
        await using var stream = await context.OpenContentAsync(request.Content.Value, token).ConfigureAwait(false);
        var command = await JsonSerializer.DeserializeAsync<SkillCommand>(stream, Json, token).ConfigureAwait(false)
            ?? throw new SunderRpcException(new(SunderRpcErrorKind.Domain, "skills.owner.operation-failed", "The Skills owner request is invalid."));
        return await Write(context, await operations.CommandAsync(command, token).ConfigureAwait(false), token).ConfigureAwait(false);
    }

    [SunderRpcMethod(Idempotency = SunderRpcMethodIdempotency.Idempotent, Errors = ["skills.owner.operation-failed"])]
    public async IAsyncEnumerable<OwnerEmpty> ChangesAsync(OwnerEmpty request, [EnumeratorCancellation] CancellationToken token = default)
    {
        await foreach (var _ in operations.SubscribeChangesAsync(token).WithCancellation(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return new();
        }
    }

    private static async ValueTask<OwnerContent> Write(SunderRpcInvocationContext context, SkillProjection value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await context.RegisterContentAsync(stream, new("application/json", "skills-projection.json", bytes.LongLength,
            Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1), token).ConfigureAwait(false);
        return new() { Content = new() { Value = content } };
    }
}

[SunderRpcTransparent]
internal sealed class ContentReference { public required SunderRpcContentReference Value { get; init; } }
internal sealed class OwnerEmpty;
internal sealed class OwnerContent { public required ContentReference Content { get; init; } }
