using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed class HistoryEmbeddingProviderCatalog(
    IServiceProviders<AgentServices.IEmbeddingProviderService> embeddingProviders)
{
    internal async Task<IReadOnlyList<HistoryEmbeddingProviderOption>> ListOptionsAsync(
        CancellationToken cancellationToken = default)
        => (await SnapshotProvidersAsync(cancellationToken).ConfigureAwait(false))
            .Select(static item => new HistoryEmbeddingProviderOption(
                item.PackageId,
                item.ProviderId,
                Bound(item.DisplayName, HistorySearchLimits.MaximumDisplayCharacters)))
            .GroupBy(static item => (item.PackageId, item.ProviderId), OwnedProviderKeyComparer.Instance)
            .Where(static group => group.Count() == 1)
            .Select(static group => group.Single())
            .OrderBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.PackageId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static item => item.ProviderId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal async Task<OwnedEmbeddingProvider> ResolveAsync(
        string? packageId, string? providerId, CancellationToken cancellationToken = default)
    {
        var normalizedProviderId = NormalizeRequired(providerId, "An embedding provider id is required.");
        var normalizedPackageId = NormalizeOptional(packageId);
        var matches = (await SnapshotProvidersAsync(cancellationToken).ConfigureAwait(false))
            .Where(item => string.Equals(
                item.ProviderId.Trim(),
                normalizedProviderId,
                StringComparison.OrdinalIgnoreCase))
            .Where(item => normalizedPackageId is null || string.Equals(
                item.PackageId.Trim(),
                normalizedPackageId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException("The selected embedding provider is unavailable.");
        }
        if (matches.Length != 1)
        {
            throw new InvalidOperationException(
                "The embedding provider id is ambiguous. Select the provider by its owning package.");
        }

        return matches[0];
    }

    internal async Task<ResolvedEmbeddingSelection> ResolveSelectionAsync(
        string? packageId,
        string? providerId,
        string? modelId,
        CancellationToken cancellationToken)
    {
        var owned = await ResolveAsync(packageId, providerId, cancellationToken).ConfigureAwait(false);
        var requestedModelId = NormalizeModelId(modelId);
        var models = await owned.Provider.Service.ListModelsAsync(cancellationToken).ConfigureAwait(false);
        var matches = models
            .Where(model => string.Equals(model.ModelId?.Trim(), requestedModelId, StringComparison.Ordinal))
            .ToArray();
        if (matches.Length == 0)
        {
            throw new InvalidOperationException("The selected embedding model is unavailable.");
        }
        if (matches.Length != 1)
        {
            throw new InvalidOperationException("The selected embedding model id is ambiguous.");
        }
        if (matches[0].Dimensions is <= 0 or > HistorySearchLimits.MaximumVectorDimensions)
        {
            throw new InvalidOperationException("The selected embedding model declares unsupported dimensions.");
        }

        var resolvedModelId = matches[0].ModelId;
        var selection = new ResolvedEmbeddingSelection(
            owned,
            resolvedModelId,
            matches[0],
            string.Empty);
        return selection with
        {
            SpaceFingerprint = await GetSpaceFingerprintAsync(selection, cancellationToken).ConfigureAwait(false),
        };
    }

    internal async Task<string> GetSpaceFingerprintAsync(
        ResolvedEmbeddingSelection selection,
        CancellationToken cancellationToken)
    {
        var providerSpaceIdentity = await selection.OwnedProvider.Provider.Service
            .SpaceIdentityAsync(selection.ModelId, cancellationToken).ConfigureAwait(false);
        return CreateSpaceFingerprint(
            selection.OwnedProvider,
            selection.ModelId,
            selection.Model,
            providerSpaceIdentity);
    }

    private async Task<IReadOnlyList<OwnedEmbeddingProvider>> SnapshotProvidersAsync(
        CancellationToken cancellationToken)
    {
        ServiceProviderCatalog<AgentServices.IEmbeddingProviderService> snapshot;
        try
        {
            snapshot = await embeddingProviders.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (
            !cancellationToken.IsCancellationRequested
            && exception.Error.Kind == SunderRpcErrorKind.Unavailable)
        {
            return [];
        }

        var providers = new List<OwnedEmbeddingProvider>();
        foreach (var provider in snapshot.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active)
            {
                continue;
            }

            AgentEmbeddingProviderDescriptor descriptor;
            try
            {
                descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (SunderRpcException exception) when (
                !cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                continue;
            }
            if (string.IsNullOrWhiteSpace(descriptor.ProviderId))
            {
                continue;
            }

            providers.Add(new OwnedEmbeddingProvider(
                provider.PackageId,
                descriptor.ProviderId,
                descriptor.DisplayName,
                string.Join('\n',
                    provider.PackageVersion,
                    provider.ContractVersion,
                    provider.DescriptorSha256,
                    provider.AbiSha256,
                    provider.ProviderId),
                provider));
        }

        return providers;
    }

    private static string NormalizeRequired(string? value, string message)
        => NormalizeOptional(value) ?? throw new InvalidOperationException(message);

    private static string NormalizeModelId(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException("An embedding model id is required.")
            : value.Trim();

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string CreateSpaceFingerprint(
        OwnedEmbeddingProvider owned,
        string modelId,
        AgentEmbeddingModelDescriptor model,
        string? providerSpaceIdentity)
    {
        var source = string.Join('\n',
            owned.PackageId,
            owned.ProviderId,
            modelId,
            model.Dimensions?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            model.IsRecommended ? "1" : "0",
            providerSpaceIdentity?.Trim() ?? string.Empty,
            owned.ImplementationIdentity);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string Bound(string? value, int maximumLength)
    {
        var normalized = HistorySearchText.NormalizeStoredText(value).Trim();
        if (normalized.Length == 0)
        {
            normalized = "Embedding provider";
        }
        return HistorySearchText.BoundAtRuneBoundary(normalized, maximumLength);
    }

    private sealed class OwnedProviderKeyComparer : IEqualityComparer<(string PackageId, string ProviderId)>
    {
        internal static OwnedProviderKeyComparer Instance { get; } = new();

        public bool Equals(
            (string PackageId, string ProviderId) left,
            (string PackageId, string ProviderId) right)
            => string.Equals(left.PackageId.Trim(), right.PackageId.Trim(), StringComparison.OrdinalIgnoreCase)
               && string.Equals(left.ProviderId.Trim(), right.ProviderId.Trim(), StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string PackageId, string ProviderId) value)
            => HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.PackageId.Trim()),
                StringComparer.OrdinalIgnoreCase.GetHashCode(value.ProviderId.Trim()));
    }
}

internal sealed record OwnedEmbeddingProvider(
    string PackageId,
    string ProviderId,
    string DisplayName,
    string ImplementationIdentity,
    ServiceProvider<AgentServices.IEmbeddingProviderService> Provider);

internal sealed record ResolvedEmbeddingSelection(
    OwnedEmbeddingProvider OwnedProvider,
    string ModelId,
    AgentEmbeddingModelDescriptor Model,
    string SpaceFingerprint);
