using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.HistorySearch;

internal sealed class HistoryEmbeddingProviderCatalog(IPackageExtensionCatalog extensions)
{
    private readonly IPackageExtensionInvocationCatalog _invocations =
        extensions as IPackageExtensionInvocationCatalog
        ?? throw new InvalidOperationException(
            "The host extension catalog does not support activation-scoped invocation leases.");

    internal IReadOnlyList<HistoryEmbeddingProviderOption> ListOptions()
        => SnapshotProviders()
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

    internal OwnedEmbeddingProvider Resolve(string? packageId, string? providerId)
    {
        var normalizedProviderId = NormalizeRequired(providerId, "An embedding provider id is required.");
        var normalizedPackageId = NormalizeOptional(packageId);
        var matches = SnapshotProviders()
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
        var owned = Resolve(packageId, providerId);
        var requestedModelId = NormalizeModelId(modelId);
        var models = await InvokeAsync(
            owned,
            cancellationToken,
            static (provider, token) => provider.GetAvailableModelsAsync(token)).ConfigureAwait(false);
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
        var providerSpaceIdentity = await InvokeAsync(
            selection.OwnedProvider,
            cancellationToken,
            (provider, token) => provider is IAgentEmbeddingSpaceIdentityProvider identityProvider
                ? identityProvider.GetEmbeddingSpaceIdentityAsync(selection.ModelId, token)
                : ValueTask.FromResult(string.Empty)).ConfigureAwait(false);
        return CreateSpaceFingerprint(
            selection.OwnedProvider,
            selection.ModelId,
            selection.Model,
            providerSpaceIdentity);
    }

    internal ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(
        ResolvedEmbeddingSelection selection,
        CancellationToken cancellationToken)
        => InvokeAsync(
            selection.OwnedProvider,
            cancellationToken,
            static (provider, token) => provider.GetReadinessAsync(token));

    internal ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        ResolvedEmbeddingSelection selection,
        string text,
        CancellationToken cancellationToken)
        => InvokeAsync(
            selection.OwnedProvider,
            cancellationToken,
            (provider, token) => provider.GenerateEmbeddingAsync(selection.ModelId, text, token));

    internal ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        ResolvedEmbeddingSelection selection,
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
        => InvokeAsync(
            selection.OwnedProvider,
            cancellationToken,
            (provider, token) => provider.GenerateEmbeddingsAsync(selection.ModelId, texts, token));

    private IReadOnlyList<OwnedEmbeddingProvider> SnapshotProviders()
    {
        var providers = new List<OwnedEmbeddingProvider>();
        foreach (var reference in _invocations.GetExtensionReferences(PackageExtensionPoints.EmbeddingProviders))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }
            using (lease)
            {
                var descriptor = lease.Contribution.Descriptor;
                if (lease.RetirementToken.IsCancellationRequested
                    || string.IsNullOrWhiteSpace(descriptor.ProviderId))
                {
                    continue;
                }

                var providerType = lease.Contribution.GetType();
                providers.Add(new OwnedEmbeddingProvider(
                    lease.PackageId,
                    descriptor.ProviderId,
                    descriptor.DisplayName,
                    string.Join('\n',
                        providerType.Assembly.FullName,
                        providerType.FullName,
                        providerType.Module.ModuleVersionId.ToString("D")),
                    reference));
            }
        }

        return providers;
    }

    private static async ValueTask<TResult> InvokeAsync<TResult>(
        OwnedEmbeddingProvider owned,
        CancellationToken cancellationToken,
        Func<IAgentEmbeddingProvider, CancellationToken, ValueTask<TResult>> callback)
    {
        if (!owned.Reference.TryAcquire(out var lease))
        {
            throw new OperationCanceledException(
                $"Embedding provider package '{owned.PackageId}' is unavailable.");
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
                    throw new OperationCanceledException(
                        $"Embedding provider package '{owned.PackageId}' became unavailable while the callback was running.");
                }
                return result;
            }
            catch (OperationCanceledException exception) when (
                retirementToken.IsCancellationRequested
                && !cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(
                    $"Embedding provider package '{owned.PackageId}' became unavailable while the callback was running.",
                    exception);
            }
        }
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
    IPackageExtensionReference<IAgentEmbeddingProvider> Reference);

internal sealed record ResolvedEmbeddingSelection(
    OwnedEmbeddingProvider OwnedProvider,
    string ModelId,
    AgentEmbeddingModelDescriptor Model,
    string SpaceFingerprint);
