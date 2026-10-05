using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Management;

namespace Sunder.Package.Agent.Subagents.Runtime;

internal sealed class SubagentOwnerClient(ISunderRpcClient rpc) : ISubagentOwnerClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public ValueTask<SubagentProjection> QueryAsync(SubagentQuery request, CancellationToken token = default)
        => Invoke(request, "subagents-query.json", static (client, content, cancellation) => client.QueryAsync(content, cancellation), token);

    public ValueTask<SubagentProjection> CommandAsync(SubagentCommand request, CancellationToken token = default)
        => Invoke(request, "subagents-command.json", static (client, content, cancellation) => client.CommandAsync(content, cancellation), token);

    private async ValueTask<SubagentProjection> Invoke<T>(T request, string fileName,
        Func<Wire.SubagentsManagementClient, Wire.OwnerContent, CancellationToken, ValueTask<Wire.OwnerContent>> invoke, CancellationToken token) where T : notnull
    {
        var api = new PackageApi(rpc).Management;
        var provider = await Resolve(api, token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var client = api.CreateClient(scope, provider);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request, Json);
        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await client.RegisterContentAsync(stream,
            new("application/json", fileName, bytes.LongLength, Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1), token).ConfigureAwait(false);
        var response = await invoke(client.SSubagents_2dmanagement, new() { Content = content }, token).ConfigureAwait(false);
        return await Read<SubagentProjection>(scope, response, token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SubagentChanged> SubscribeChangesAsync(SubagentChangeSubscription request, [EnumeratorCancellation] CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        var provider = await Resolve(api, token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var client = api.CreateClient(scope, provider).SSubagents_2dmanagement;
        await foreach (var item in client.ChangesAsync(new Wire.ChangeSubscription { AfterRevision = request.AfterRevision }, token).WithCancellation(token).ConfigureAwait(false))
            yield return await Read<SubagentChanged>(scope, item, token).ConfigureAwait(false);
    }

    private static async ValueTask<Sunder.Generated.Rpc.PackageApis.CSunder_Package_Agent_Subagents_Owner.Provider> Resolve(
        Sunder.Generated.Rpc.PackageApis.CSunder_Package_Agent_Subagents_Owner.Contract api, CancellationToken token)
    {
        try
        {
            var catalog = await api.ListAsync(token).ConfigureAwait(false);
            return catalog.Providers.SingleOrDefault(provider =>
                provider.PackageId == "sunder.package.agent.subagents"
                && provider.ProviderId == "subagents.management"
                && provider.State == SunderRpcProviderState.Active)
                ?? throw new SunderRpcException(new(SunderRpcErrorKind.NotFound, "rpc.provider-not-found",
                    "The Subagents runtime is unavailable."));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found")
        { throw new InvalidOperationException("The Subagents runtime is unavailable.", error); }
    }

    private static async ValueTask<T> Read<T>(ISunderRpcCallScope scope, Wire.OwnerContent content, CancellationToken token) where T : notnull
    {
        await using var stream = await scope.OpenContentAsync(content.Content, token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, Json, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Subagents runtime returned an empty response.");
    }
}
