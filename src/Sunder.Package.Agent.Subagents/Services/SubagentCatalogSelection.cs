using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Subagents.Services;

// Optional contributions may disappear; permission and malformed-value failures
// retain their classifications rather than becoming empty editor catalogs.
internal static class SubagentCatalogSelection
{
    internal static async ValueTask<IReadOnlyList<SubagentCatalogEntry<TService, TMetadata>>> SnapshotAsync<TService, TMetadata>(
        IServiceProviders<TService> providers, CancellationToken token, Func<TService, CancellationToken, ValueTask<TMetadata>> describe)
        where TService : class where TMetadata : notnull
    {
        var result = new List<SubagentCatalogEntry<TService, TMetadata>>();
        ServiceProviderCatalog<TService> snapshot;
        try { snapshot = await providers.ListAsync(token).ConfigureAwait(false); }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Cancelled) { throw Cancelled(error, token); }
        foreach (var provider in snapshot.Providers.Where(value => value.State == ServiceProviderState.Active))
        {
            var metadata = await InvokeOptionalAsync(provider, token, describe).ConfigureAwait(false);
            if (metadata.IsAvailable) result.Add(new(provider, metadata.Value));
        }
        return result;
    }

    internal static ValueTask<SubagentCatalogResult<TResult>> InvokeOptionalAsync<TService, TMetadata, TResult>(
        SubagentCatalogEntry<TService, TMetadata> entry, CancellationToken token, Func<TService, CancellationToken, ValueTask<TResult>> invoke)
        where TService : class where TResult : notnull
        => InvokeOptionalAsync(entry.Provider, token, invoke);

    internal static async ValueTask<SubagentCatalogResult<TResult>> InvokeOptionalAsync<TService, TResult>(
        ServiceProvider<TService> provider, CancellationToken token, Func<TService, CancellationToken, ValueTask<TResult>> invoke)
        where TService : class where TResult : notnull
    {
        token.ThrowIfCancellationRequested();
        var retirement = provider.RetirementToken;
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(token, retirement);
        try
        {
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var value = await invoke(provider.Service, invocation.Token).ConfigureAwait(false);
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            return new(true, value);
        }
        catch (OperationCanceledException error) when (token.IsCancellationRequested)
        {
            if (error.CancellationToken == token) throw;
            // The generated client receives our linked token. Keep its SDK cause
            // directly visible while restoring the business caller's identity.
            throw new OperationCanceledException(error.Message, error.InnerException, token);
        }
        catch (Exception error) when (IsUnavailable(error, retirement, token)) { return new(false, default!); }
        catch (SunderRpcException error) when (error.Error.Kind == SunderRpcErrorKind.Cancelled) { throw Cancelled(error, token); }
    }

    internal static bool IsUnavailable(Exception error, CancellationToken retirement, CancellationToken caller)
        => !caller.IsCancellationRequested && (error is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable }
            || retirement.IsCancellationRequested && error is OperationCanceledException or SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled });
    internal static OperationCanceledException Cancelled(SunderRpcException error, CancellationToken caller)
        => new(error.Message, error, caller.IsCancellationRequested ? caller : CancellationToken.None);
}

internal sealed record SubagentCatalogEntry<TService, TMetadata>(ServiceProvider<TService> Provider, TMetadata Metadata) where TService : class
{
    internal string PackageId => Provider.PackageId;
}
internal readonly record struct SubagentCatalogResult<T>(bool IsAvailable, T Value) where T : notnull;
