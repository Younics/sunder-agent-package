using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

internal static class AgentExtensionInvocation
{
    internal const string PackageUnavailableMessage =
        "The selected package became unavailable while the operation was running.";

    internal static IPackageExtensionInvocationCatalog Require(IPackageExtensionCatalog catalog)
        => catalog as IPackageExtensionInvocationCatalog
           ?? throw new InvalidOperationException(
               "The host extension catalog does not support activation-scoped invocation leases.");

    internal static IReadOnlyList<AgentExtensionReference<TContract, TMetadata>> Snapshot<TContract, TMetadata>(
        IPackageExtensionInvocationCatalog catalog,
        PackageExtensionPoint<TContract> extensionPoint,
        Func<TContract, TMetadata> project)
    {
        var snapshots = new List<AgentExtensionReference<TContract, TMetadata>>();
        foreach (var reference in catalog.GetExtensionReferences(extensionPoint))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            using (lease)
            {
                var packageId = lease.PackageId;
                var metadata = project(lease.Contribution);
                if (!lease.RetirementToken.IsCancellationRequested)
                {
                    snapshots.Add(new AgentExtensionReference<TContract, TMetadata>(
                        reference,
                        packageId,
                        metadata));
                }
            }
        }

        return snapshots;
    }

    internal static async ValueTask<TResult> InvokeAsync<TContract, TMetadata, TResult>(
        AgentExtensionReference<TContract, TMetadata> reference,
        CancellationToken cancellationToken,
        Func<TContract, CancellationToken, ValueTask<TResult>> callback)
    {
        if (!reference.Reference.TryAcquire(out var lease))
        {
            throw Unavailable(reference.PackageId);
        }

        using (lease)
        {
            var retirementToken = lease.RetirementToken;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                var result = await callback(lease.Contribution, invocation.Token).ConfigureAwait(false);
                if (retirementToken.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
                {
                    throw Unavailable(reference.PackageId);
                }

                return result;
            }
            catch (AgentPackageUnavailableException)
            {
                throw;
            }
            catch (OperationCanceledException exception) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw Unavailable(reference.PackageId, exception);
            }
            catch (Exception exception) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw Unavailable(reference.PackageId, exception);
            }
        }
    }

    internal static async ValueTask InvokeAsync<TContract, TMetadata>(
        AgentExtensionReference<TContract, TMetadata> reference,
        CancellationToken cancellationToken,
        Func<TContract, CancellationToken, ValueTask> callback)
    {
        await InvokeAsync(
            reference,
            cancellationToken,
            async (contribution, token) =>
            {
                await callback(contribution, token).ConfigureAwait(false);
                return true;
            }).ConfigureAwait(false);
    }

    internal static AgentPackageUnavailableException Unavailable(
        string packageId,
        Exception? innerException = null)
        => new(packageId, innerException);
}

internal record AgentExtensionReference<TContract, TMetadata>(
    IPackageExtensionReference<TContract> Reference,
    string PackageId,
    TMetadata Metadata);

internal sealed class AgentPackageUnavailableException : OperationCanceledException
{
    internal AgentPackageUnavailableException(string packageId, Exception? innerException = null)
        : base(
            $"Package '{packageId}' became unavailable while its extension callback was running.",
            innerException)
    {
        PackageId = packageId;
    }

    internal string PackageId { get; }
}
