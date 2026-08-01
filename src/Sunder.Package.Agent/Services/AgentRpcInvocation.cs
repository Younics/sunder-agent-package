using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Services;

internal static class AgentRpcInvocation
{
    internal const string PackageUnavailableMessage =
        "The selected package became unavailable while the operation was running.";

    internal static AgentPackageUnavailableException Unavailable(
        string packageId,
        Exception? innerException = null)
        => new(packageId, innerException);

    internal static bool IsUnavailableFailure(
        Exception exception,
        CancellationToken callerCancellationToken)
    {
        if (exception is AgentPackageUnavailableException)
        {
            return true;
        }
        if (callerCancellationToken.IsCancellationRequested)
        {
            return false;
        }
        return exception is SunderRpcException
               {
                   Error.Kind: SunderRpcErrorKind.StaleEndpoint
                       or SunderRpcErrorKind.Unavailable,
               };
    }

    internal static IReadOnlyList<AgentRpcOwnedReference<TService, TMetadata>> Snapshot<TService, TMetadata>(
        AgentRpcCatalog catalog,
        AgentRpcService<TService> service,
        Func<TService, TMetadata> metadataSelector)
        where TService : class
    {
        var snapshots = new List<AgentRpcOwnedReference<TService, TMetadata>>();
        foreach (var reference in catalog.GetServiceReferences(service))
        {
            if (!reference.TryAcquire(out var lease)) continue;
            using (lease)
            {
                var metadata = metadataSelector(lease.Service);
                if (!lease.RetirementToken.IsCancellationRequested)
                {
                    snapshots.Add(new AgentRpcOwnedReference<TService, TMetadata>(reference, lease.PackageId, metadata));
                }
            }
        }
        return snapshots;
    }

    internal static IReadOnlyList<AgentRpcOwnedProviderReference<TService, TMetadata>> Snapshot<TService, TMetadata>(
        AgentRpcCatalog catalog,
        AgentRpcProviderService<TService> service,
        Func<TService, TMetadata> metadataSelector)
        where TService : class
    {
        var snapshots = new List<AgentRpcOwnedProviderReference<TService, TMetadata>>();
        foreach (var reference in catalog.GetServiceReferences(service))
        {
            if (!reference.TryAcquire(out var lease)) continue;
            using (lease)
            {
                var metadata = metadataSelector(lease.Service);
                if (!lease.RetirementToken.IsCancellationRequested)
                {
                    snapshots.Add(new AgentRpcOwnedProviderReference<TService, TMetadata>(reference, lease.PackageId, metadata));
                }
            }
        }
        return snapshots;
    }

    internal static async ValueTask<TResult> InvokeAsync<TService, TMetadata, TResult>(
        AgentRpcOwnedReference<TService, TMetadata> reference,
        CancellationToken cancellationToken,
        Func<TService, CancellationToken, ValueTask<TResult>> callback)
        where TService : class
    {
        if (!reference.Reference.TryAcquire(out var lease))
        {
            throw Unavailable(reference.PackageId);
        }
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetirementToken))
        {
            try
            {
                var result = await callback(lease.Service, invocation.Token).ConfigureAwait(false);
                if (lease.RetirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw Unavailable(reference.PackageId);
                }
                return result;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                throw Unavailable(reference.PackageId, exception);
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw new OperationCanceledException(exception.Message, exception, cancellationToken);
            }
        }
    }

    internal static async ValueTask InvokeAsync<TService, TMetadata>(
        AgentRpcOwnedReference<TService, TMetadata> reference,
        CancellationToken cancellationToken,
        Func<TService, CancellationToken, ValueTask> callback)
        where TService : class
    {
        await InvokeAsync(
            reference,
            cancellationToken,
            async (service, token) =>
            {
                await callback(service, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
    }

    internal static async ValueTask<TResult> InvokeAsync<TService, TMetadata, TResult>(
        AgentRpcOwnedProviderReference<TService, TMetadata> reference,
        CancellationToken cancellationToken,
        Func<TService, CancellationToken, ValueTask<TResult>> callback)
        where TService : class
    {
        if (!reference.Reference.TryAcquire(out var lease))
        {
            throw Unavailable(reference.PackageId);
        }
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetirementToken))
        {
            try
            {
                var result = await callback(lease.Service, invocation.Token).ConfigureAwait(false);
                if (lease.RetirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw Unavailable(reference.PackageId);
                }
                return result;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                throw Unavailable(reference.PackageId, exception);
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw new OperationCanceledException(exception.Message, exception, cancellationToken);
            }
        }
    }
}

internal sealed record AgentRpcOwnedReference<TService, TMetadata>(
    AgentRpcReference<TService> Reference,
    string PackageId,
    TMetadata Metadata)
    where TService : class;

internal sealed record AgentRpcOwnedProviderReference<TService, TMetadata>(
    AgentRpcProviderReference<TService> Reference,
    string PackageId,
    TMetadata Metadata)
    where TService : class;

internal sealed class AgentPackageUnavailableException : OperationCanceledException
{
    internal AgentPackageUnavailableException(string packageId, Exception? innerException = null)
        : base(
            $"Package '{packageId}' became unavailable while its RPC endpoint was running.",
            innerException)
    {
        PackageId = packageId;
    }

    internal string PackageId { get; }
}
