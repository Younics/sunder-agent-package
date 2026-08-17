using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Sunder.Package.Agent.Subagents.Protocol;
using Sunder.Package.Agent.Subagents.Protocol.Generated;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Runtime;

#if SUNDER_SUBAGENTS_APP
internal sealed class SubagentOwnerClient(ISunderRpcClient rpc) : ISubagentOwnerClient
{
    public async ValueTask<SubagentProjection> QueryAsync(
        SubagentQuery request,
        CancellationToken cancellationToken = default)
        => await InvokeAsync(
            request,
            "subagents-query.json",
            static (client, content, token) => client.QueryAsync(content, token),
            cancellationToken).ConfigureAwait(false);

    public async ValueTask<SubagentProjection> CommandAsync(
        SubagentCommand request,
        CancellationToken cancellationToken = default)
        => await InvokeAsync(
            request,
            "subagents-command.json",
            static (client, content, token) => client.CommandAsync(content, token),
            cancellationToken).ConfigureAwait(false);

    public async IAsyncEnumerable<SubagentChanged> SubscribeChangesAsync(
        SubagentChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var client = new SubagentsManagementClient(scope, endpoint);
        await foreach (var item in client.ChangesAsync(
                           new ChangeSubscription { AfterRevision = request.AfterRevision },
                           cancellationToken).ConfigureAwait(false))
        {
            yield return await ReadContentAsync<SubagentChanged>(scope, item, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<SubagentProjection> InvokeAsync<TRequest>(
        TRequest request,
        string fileName,
        Func<SubagentsManagementClient, OwnerContent, CancellationToken, ValueTask<OwnerContent>> invoke,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        var endpoint = await ResolveEndpointAsync(cancellationToken).ConfigureAwait(false);
        await using var scope = await rpc.CreateCallScopeAsync(cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var client = new SubagentsManagementClient(scope, endpoint);
        var content = await RegisterContentAsync(
            scope,
            endpoint,
            request,
            fileName,
            cancellationToken).ConfigureAwait(false);
        var response = await invoke(client, content, cancellationToken).ConfigureAwait(false);
        return await ReadContentAsync<SubagentProjection>(scope, response, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<SunderRpcEndpointReference> ResolveEndpointAsync(
        CancellationToken cancellationToken)
    {
        var descriptor = SubagentsOwnerProtocol.Descriptor;
        var catalog = await rpc.DiscoverAsync(descriptor.ContractId, cancellationToken).ConfigureAwait(false);
        return catalog.Providers.SingleOrDefault(provider =>
                   string.Equals(provider.ProviderId, SubagentsOwnerProtocol.ProviderId, StringComparison.Ordinal)
                   && provider.State == SunderRpcProviderState.Active
                   && string.Equals(provider.AbiSha256, descriptor.AbiSha256, StringComparison.Ordinal))
               ?.Endpoint
               ?? throw new InvalidOperationException("The Subagents runtime is unavailable.");
    }

    private static async ValueTask<OwnerContent> RegisterContentAsync<T>(
        ISunderRpcCallScope scope,
        SunderRpcEndpointReference endpoint,
        T value,
        string fileName,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SubagentOwnerJson.Options);
        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await scope.RegisterContentAsync(
            endpoint,
            stream,
            SubagentOwnerJson.ContentOptions(fileName, bytes.LongLength),
            cancellationToken).ConfigureAwait(false);
        return new OwnerContent { Content = content };
    }

    private static async ValueTask<T> ReadContentAsync<T>(
        ISunderRpcCallScope scope,
        OwnerContent response,
        CancellationToken cancellationToken)
        where T : notnull
    {
        await using var stream = await scope.OpenContentAsync(response.Content, cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(
                   stream,
                   SubagentOwnerJson.Options,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new InvalidDataException("The Subagents runtime returned an empty response.");
    }
}
#endif

#if SUNDER_SUBAGENTS_WORKER
internal sealed class SubagentOwnerProvider(
    SubagentRuntimeHandler handler,
    SubagentRuntimeChangeStream changes) : ISubagentsManagementProvider
{
    public ValueTask<OwnerContent> QueryAsync(
        SunderRpcInvocationContext context,
        OwnerContent request,
        CancellationToken cancellationToken = default)
        => InvokeAsync<SubagentQuery>(
            context,
            request,
            handler.HandleAsync,
            "subagents-projection.json",
            cancellationToken);

    public ValueTask<OwnerContent> CommandAsync(
        SunderRpcInvocationContext context,
        OwnerContent request,
        CancellationToken cancellationToken = default)
        => InvokeAsync<SubagentCommand>(
            context,
            request,
            handler.HandleAsync,
            "subagents-projection.json",
            cancellationToken);

    public async IAsyncEnumerable<OwnerContent> ChangesAsync(
        SunderRpcInvocationContext context,
        ChangeSubscription request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (var change in changes.SubscribeAsync(
                           new SubagentChangeSubscription(request.AfterRevision),
                           cancellationToken).ConfigureAwait(false))
        {
            var content = await TryWriteChangeContentAsync(
                context,
                change,
                "subagents-change.json",
                cancellationToken).ConfigureAwait(false);
            if (content is null)
            {
                yield break;
            }
            yield return content;
        }
    }

    private static async ValueTask<OwnerContent> InvokeAsync<TRequest>(
        SunderRpcInvocationContext context,
        OwnerContent request,
        Func<TRequest, CancellationToken, ValueTask<SubagentProjection>> invoke,
        string fileName,
        CancellationToken cancellationToken)
        where TRequest : notnull
    {
        try
        {
            var value = await ReadContentAsync<TRequest>(context, request, cancellationToken)
                .ConfigureAwait(false);
            return await WriteContentAsync(
                context,
                await invoke(value, cancellationToken).ConfigureAwait(false),
                fileName,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (SubagentOwnerJson.ShouldMap(exception))
        {
            throw SubagentOwnerJson.Map((SubagentOwnerOperationException)exception);
        }
    }

    private static async ValueTask<T> ReadContentAsync<T>(
        SunderRpcInvocationContext context,
        OwnerContent request,
        CancellationToken cancellationToken)
        where T : notnull
    {
        await using var stream = await context.OpenContentAsync(request.Content, cancellationToken)
            .ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(
                   stream,
                   SubagentOwnerJson.Options,
                   cancellationToken).ConfigureAwait(false)
               ?? throw new SubagentOwnerOperationException(
                   "The Subagents owner request is invalid.");
    }

    private static async ValueTask<OwnerContent> WriteContentAsync<T>(
        SunderRpcInvocationContext context,
        T value,
        string fileName,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SubagentOwnerJson.Options);
        await using var stream = new MemoryStream(bytes, writable: false);
        var content = await context.RegisterContentAsync(
            stream,
            SubagentOwnerJson.ContentOptions(fileName, bytes.LongLength),
            cancellationToken).ConfigureAwait(false);
        return new OwnerContent { Content = content };
    }

    private static async ValueTask<OwnerContent?> TryWriteChangeContentAsync<T>(
        SunderRpcInvocationContext context,
        T value,
        string fileName,
        CancellationToken cancellationToken)
        where T : notnull
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, SubagentOwnerJson.Options);
        await using var stream = new MemoryStream(bytes, writable: false);
        // Keep the retirement boundary around the Host-owned authority call only.
        try
        {
            var content = await context.RegisterContentAsync(
                stream,
                SubagentOwnerJson.ContentOptions(fileName, bytes.LongLength),
                cancellationToken).ConfigureAwait(false);
            return new OwnerContent { Content = content };
        }
        catch (SunderRpcException exception) when (IsContentRegistrationRetirement(exception))
        {
            return null;
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested
            || context.CancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }

    private static bool IsContentRegistrationRetirement(SunderRpcException exception)
        => exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint
            or SunderRpcErrorKind.Cancelled
            or SunderRpcErrorKind.Unavailable
            or SunderRpcErrorKind.DeadlineExceeded;
}
#endif

internal static class SubagentOwnerJson
{
    internal static JsonSerializerOptions Options { get; } = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    internal static SunderRpcContentRegistrationOptions ContentOptions(string fileName, long length)
        => new(
            "application/json",
            fileName,
            length,
            Repeatability: SunderRpcContentRepeatability.SingleUse,
            MaximumUses: 1);

    internal static bool ShouldMap(Exception exception)
        => exception is SubagentOwnerOperationException;

    internal static SunderRpcException Map(SubagentOwnerOperationException exception)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? "The requested Subagents operation failed."
            : exception.Message;
        if (message.Length > 2_048)
        {
            message = message[..2_048];
        }
        return new SunderRpcException(new SunderRpcError(
            SunderRpcErrorKind.Domain,
            "subagents.owner.operation-failed",
            message));
    }
}

internal sealed class SubagentOwnerOperationException(
    string message,
    Exception? innerException = null) : InvalidOperationException(message, innerException);
