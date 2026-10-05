using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Generated.Rpc;
using Sunder.Sdk.Rpc;
using Wire = Sunder.Generated.Rpc.Management;

namespace Sunder.Package.Agent.Skills.Runtime;

internal sealed class SkillOwnerClient(ISunderRpcClient rpc) : ISkillOwnerClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow,
    };

    public async ValueTask<SkillProjection> QueryAsync(CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        var provider = await Resolve(token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var result = await api.CreateClient(scope, provider).SSkills_2dmanagement.QueryAsync(new Wire.OwnerEmpty(), token).ConfigureAwait(false);
        return await Read(scope, result, token).ConfigureAwait(false);
    }

    public async ValueTask<SkillProjection> CommandAsync(SkillCommand command, CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        var provider = await Resolve(token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        var client = api.CreateClient(scope, provider);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(command, Json);
        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await client.RegisterContentAsync(stream, new("application/json", "skills-command.json", bytes.LongLength,
            Repeatability: SunderRpcContentRepeatability.SingleUse, MaximumUses: 1), token).ConfigureAwait(false);
        var result = await client.SSkills_2dmanagement.CommandAsync(new Wire.OwnerContent { Content = content }, token).ConfigureAwait(false);
        return await Read(scope, result, token).ConfigureAwait(false);
    }

    public async IAsyncEnumerable<SkillChanged> SubscribeChangesAsync([EnumeratorCancellation] CancellationToken token = default)
    {
        var api = new PackageApi(rpc).Management;
        var provider = await Resolve(token).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: token).ConfigureAwait(false);
        await foreach (var _ in api.CreateClient(scope, provider).SSkills_2dmanagement.ChangesAsync(new Wire.OwnerEmpty(), token).WithCancellation(token).ConfigureAwait(false))
        {
            token.ThrowIfCancellationRequested();
            yield return new();
        }
    }

    private async ValueTask<Sunder.Generated.Rpc.PackageApis.CSunder_Package_Agent_Skills_Owner.Provider> Resolve(CancellationToken token)
    {
        try
        {
            var catalog = await new PackageApi(rpc).Management.ListAsync(token).ConfigureAwait(false);
            return catalog.Providers.SingleOrDefault(provider =>
                provider.PackageId == "sunder.package.agent.skills"
                && provider.ProviderId == "skills.management"
                && provider.State == SunderRpcProviderState.Active)
                ?? throw new SunderRpcException(new(SunderRpcErrorKind.NotFound, "rpc.provider-not-found",
                    "The Skills runtime is unavailable."));
        }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.NotFound && error.Error.Code == "rpc.provider-not-found")
        { throw new InvalidOperationException("The Skills runtime is unavailable.", error); }
    }

    private static async ValueTask<SkillProjection> Read(ISunderRpcCallScope scope, Wire.OwnerContent content, CancellationToken token)
    {
        await using var stream = await scope.OpenContentAsync(content.Content, token).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<SkillProjection>(stream, Json, token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The Skills runtime returned an empty response.");
    }
}
