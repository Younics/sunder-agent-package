using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Services;

internal static class SubagentCatalogRpcInvocation
{
    internal static IReadOnlyList<SubagentCatalogRpcReference<TService, TMetadata>> Snapshot<TService, TMetadata>(
        AgentRpcCatalog catalog,
        AgentRpcService<TService> service,
        CancellationToken cancellationToken,
        Func<TService, TMetadata> metadataSelector)
        where TService : class
    {
        var snapshots = new List<SubagentCatalogRpcReference<TService, TMetadata>>();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AgentRpcReference<TService>> references;
        try
        {
            references = catalog.GetServiceReferences(service);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw Cancelled(exception, cancellationToken);
        }
        foreach (var reference in references)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AgentRpcLease<TService>? lease;
            try
            {
                if (!reference.TryAcquire(out lease)) continue;
            }
            catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
            {
                continue;
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw Cancelled(exception, cancellationToken);
            }

            using (lease)
            {
                var retirementToken = lease.RetirementToken;
                try
                {
                    var metadata = metadataSelector(lease.Service);
                    if (!retirementToken.IsCancellationRequested)
                    {
                        snapshots.Add(new(reference, lease.PackageId, metadata));
                    }
                }
                catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
                {
                }
                catch (Exception exception) when (IsRetirementCancellation(
                    exception,
                    retirementToken,
                    cancellationToken))
                {
                }
                catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
                {
                    throw Cancelled(exception, cancellationToken);
                }
            }
        }

        return snapshots;
    }

    internal static async ValueTask<SubagentCatalogRpcResult<TResult>> InvokeOptionalAsync<TService, TMetadata, TResult>(
        SubagentCatalogRpcReference<TService, TMetadata> reference,
        CancellationToken cancellationToken,
        Func<TService, CancellationToken, ValueTask<TResult>> callback)
        where TService : class
        where TResult : notnull
    {
        cancellationToken.ThrowIfCancellationRequested();
        AgentRpcLease<TService>? lease;
        try
        {
            if (!reference.Reference.TryAcquire(out lease)) return SubagentCatalogRpcResult<TResult>.Unavailable;
        }
        catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
        {
            return SubagentCatalogRpcResult<TResult>.Unavailable;
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw Cancelled(exception, cancellationToken);
        }

        using (lease)
        {
            var retirementToken = lease.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                var result = await callback(lease.Service, invocation.Token).ConfigureAwait(false);
                return retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                    ? SubagentCatalogRpcResult<TResult>.Unavailable
                    : new SubagentCatalogRpcResult<TResult>(true, result);
            }
            catch (Exception exception) when (IsUnavailable(exception, cancellationToken))
            {
                return SubagentCatalogRpcResult<TResult>.Unavailable;
            }
            catch (Exception exception) when (IsRetirementCancellation(
                exception,
                retirementToken,
                cancellationToken))
            {
                return SubagentCatalogRpcResult<TResult>.Unavailable;
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw Cancelled(exception, cancellationToken);
            }
        }
    }

    private static bool IsUnavailable(
        Exception exception,
        CancellationToken callerCancellationToken)
        => !callerCancellationToken.IsCancellationRequested
           && exception is SunderRpcException
           {
               Error.Kind: SunderRpcErrorKind.StaleEndpoint
                   or SunderRpcErrorKind.Unavailable,
           };

    private static bool IsRetirementCancellation(
        Exception exception,
        CancellationToken retirementToken,
        CancellationToken callerCancellationToken)
        => retirementToken.IsCancellationRequested
           && !callerCancellationToken.IsCancellationRequested
           && (exception is OperationCanceledException
               || exception is SunderRpcException
               {
                   Error.Kind: SunderRpcErrorKind.Cancelled,
               });

    internal static OperationCanceledException Cancelled(
        SunderRpcException exception,
        CancellationToken callerCancellationToken)
        => new(
            exception.Message,
            exception,
            callerCancellationToken.IsCancellationRequested
                ? callerCancellationToken
                : CancellationToken.None);
}

internal sealed record SubagentCatalogRpcReference<TService, TMetadata>(
    AgentRpcReference<TService> Reference,
    string PackageId,
    TMetadata Metadata)
    where TService : class;

internal readonly record struct SubagentCatalogRpcResult<TResult>(
    bool IsAvailable,
    TResult Value)
    where TResult : notnull
{
    internal static SubagentCatalogRpcResult<TResult> Unavailable { get; } = new(false, default!);
}
