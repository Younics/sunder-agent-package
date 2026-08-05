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
        Func<TService, TMetadata> metadataSelector,
        CancellationToken cancellationToken = default,
        bool omitUnavailable = false)
        where TService : class
    {
        var snapshots = new List<AgentRpcOwnedReference<TService, TMetadata>>();
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
                if (!reference.TryAcquire(out lease))
                {
                    if (omitUnavailable) continue;
                    throw Unavailable(reference.PackageId);
                }
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                if (omitUnavailable) continue;
                throw;
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
                        snapshots.Add(new AgentRpcOwnedReference<TService, TMetadata>(reference, lease.PackageId, metadata));
                    }
                }
                catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
                {
                    if (!omitUnavailable) throw;
                }
                catch (Exception exception) when (IsRetirementCancellation(
                    exception,
                    retirementToken,
                    cancellationToken))
                {
                    if (!omitUnavailable) throw Unavailable(reference.PackageId, exception);
                }
                catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
                {
                    throw Cancelled(exception, cancellationToken);
                }
            }
        }
        return snapshots;
    }

    internal static async Task<IReadOnlyList<AgentRpcOwnedReference<TService, TMetadata>>> SnapshotAsync<TService, TMetadata>(
        AgentRpcCatalog catalog,
        AgentRpcService<TService> service,
        Func<TService, CancellationToken, ValueTask<TMetadata>> metadataSelector,
        CancellationToken cancellationToken = default,
        bool omitUnavailable = false)
        where TService : class
    {
        var snapshots = new List<AgentRpcOwnedReference<TService, TMetadata>>();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AgentRpcReference<TService>> references;
        try
        {
            references = await catalog.GetServiceReferencesAsync(service, cancellationToken)
                .ConfigureAwait(false);
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
                lease = await reference.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
                if (lease is null)
                {
                    if (omitUnavailable) continue;
                    throw Unavailable(reference.PackageId);
                }
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                if (omitUnavailable) continue;
                throw;
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
                    var metadata = await metadataSelector(lease.Service, invocation.Token)
                        .ConfigureAwait(false);
                    if (!retirementToken.IsCancellationRequested)
                    {
                        snapshots.Add(new AgentRpcOwnedReference<TService, TMetadata>(reference, lease.PackageId, metadata));
                    }
                }
                catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
                {
                    if (!omitUnavailable) throw;
                }
                catch (Exception exception) when (IsRetirementCancellation(
                    exception,
                    retirementToken,
                    cancellationToken))
                {
                    if (!omitUnavailable) throw Unavailable(reference.PackageId, exception);
                }
                catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
                {
                    throw Cancelled(exception, cancellationToken);
                }
            }
        }

        return snapshots;
    }

    internal static IReadOnlyList<AgentRpcOwnedProviderReference<TService, TMetadata>> Snapshot<TService, TMetadata>(
        AgentRpcCatalog catalog,
        AgentRpcProviderService<TService> service,
        Func<TService, TMetadata> metadataSelector,
        CancellationToken cancellationToken = default,
        bool omitUnavailable = false)
        where TService : class
    {
        var snapshots = new List<AgentRpcOwnedProviderReference<TService, TMetadata>>();
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AgentRpcProviderReference<TService>> references;
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
                if (!reference.TryAcquire(out lease))
                {
                    if (omitUnavailable) continue;
                    throw Unavailable(reference.PackageId);
                }
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                if (omitUnavailable) continue;
                throw;
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
                        snapshots.Add(new AgentRpcOwnedProviderReference<TService, TMetadata>(reference, lease.PackageId, metadata));
                    }
                }
                catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
                {
                    if (!omitUnavailable) throw;
                }
                catch (Exception exception) when (IsRetirementCancellation(
                    exception,
                    retirementToken,
                    cancellationToken))
                {
                    if (!omitUnavailable) throw Unavailable(reference.PackageId, exception);
                }
                catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
                {
                    throw Cancelled(exception, cancellationToken);
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
        AgentRpcLease<TService>? lease;
        try
        {
            lease = await reference.Reference.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
        {
            throw Unavailable(reference.PackageId, exception);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw Cancelled(exception, cancellationToken);
        }
        if (lease is null)
        {
            throw Unavailable(reference.PackageId);
        }
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetirementToken))
        {
            var retirementToken = lease.RetirementToken;
            try
            {
                var result = await callback(lease.Service, invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw Unavailable(reference.PackageId);
                }
                return result;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                throw Unavailable(reference.PackageId, exception);
            }
            catch (Exception exception) when (IsRetirementCancellation(
                exception,
                retirementToken,
                cancellationToken))
            {
                throw Unavailable(reference.PackageId, exception);
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw Cancelled(exception, cancellationToken);
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
        AgentRpcLease<TService>? lease;
        try
        {
            lease = await reference.Reference.TryAcquireAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
        {
            throw Unavailable(reference.PackageId, exception);
        }
        catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
        {
            throw Cancelled(exception, cancellationToken);
        }
        if (lease is null)
        {
            throw Unavailable(reference.PackageId);
        }
        using (lease)
        using (var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.RetirementToken))
        {
            var retirementToken = lease.RetirementToken;
            try
            {
                var result = await callback(lease.Service, invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw Unavailable(reference.PackageId);
                }
                return result;
            }
            catch (Exception exception) when (IsUnavailableFailure(exception, cancellationToken))
            {
                throw Unavailable(reference.PackageId, exception);
            }
            catch (Exception exception) when (IsRetirementCancellation(
                exception,
                retirementToken,
                cancellationToken))
            {
                throw Unavailable(reference.PackageId, exception);
            }
            catch (SunderRpcException exception) when (exception.Error.Kind == SunderRpcErrorKind.Cancelled)
            {
                throw Cancelled(exception, cancellationToken);
            }
        }
    }

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
