using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticModelRuntimeResolver : IDisposable
{
    private static readonly TimeSpan DefaultConfigurationCacheDuration = TimeSpan.FromMinutes(5);

    private readonly IPackageExtensionInvocationCatalog _invocationCatalog;
    private readonly IPackageExtensionCatalogMonitor? _catalogMonitor;
    private readonly MemorySemanticSettingsService _settingsService;
    private readonly TimeSpan _configurationCacheDuration;
    private readonly object _configurationCacheSync = new();
    private readonly Dictionary<string, CachedEmbeddingConfiguration> _configurationCache = new(StringComparer.OrdinalIgnoreCase);
    private long _catalogRevision;
    private bool _disposed;

    public SemanticModelRuntimeResolver(
        IPackageExtensionCatalog extensionCatalog,
        MemorySemanticSettingsService settingsService,
        TimeSpan? configurationCacheDuration = null)
    {
        _invocationCatalog = extensionCatalog as IPackageExtensionInvocationCatalog
            ?? throw new InvalidOperationException(
                "The host extension catalog does not support activation-scoped invocation leases.");
        _catalogMonitor = extensionCatalog as IPackageExtensionCatalogMonitor;
        _settingsService = settingsService;
        _configurationCacheDuration = configurationCacheDuration ?? DefaultConfigurationCacheDuration;
        if (_configurationCacheDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(configurationCacheDuration));
        }
        if (_catalogMonitor is not null)
        {
            _catalogMonitor.Changed += OnExtensionCatalogChanged;
        }
    }

    public Task<ResolvedEmbeddingProvider?> ResolveForProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default)
        => ResolveForProfileCoreAsync(profileId, canInvokeProvider: null, cancellationToken);

    internal Task<ResolvedEmbeddingProvider?> ResolveForProfileAsync(
        string profileId,
        Func<CancellationToken, Task<bool>> canInvokeProvider,
        CancellationToken cancellationToken)
        => ResolveForProfileCoreAsync(profileId, canInvokeProvider, cancellationToken);

    private async Task<ResolvedEmbeddingProvider?> ResolveForProfileCoreAsync(
        string profileId,
        Func<CancellationToken, Task<bool>>? canInvokeProvider,
        CancellationToken cancellationToken)
    {
        var resolved = await ResolveConfigurationForProfileAsync(
            profileId,
            cancellationToken,
            forceProviderIdentityRefresh: true,
            canInvokeProvider).ConfigureAwait(false);
        if (resolved is null)
        {
            return null;
        }

        try
        {
            if (!await CanInvokeProviderAsync(canInvokeProvider, cancellationToken).ConfigureAwait(false))
            {
                return null;
            }
            var readiness = await resolved.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
            return readiness.Status == AgentProviderReadinessStatus.Ready
                ? resolved
                : null;
        }
        catch (EmbeddingProviderRetiredException)
        {
            return null;
        }
    }

    internal async Task<ResolvedEmbeddingProvider?> ResolveConfigurationForProfileAsync(
        string profileId,
        CancellationToken cancellationToken = default,
        bool forceProviderIdentityRefresh = false,
        Func<CancellationToken, Task<bool>>? canInvokeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        var binding = InvokeRuntimeCatalog(
            catalog => catalog.GetModelBinding(profileId, AgentModelCapabilityKinds.Embedding)
                       ?? ResolveLegacyBinding(catalog.GetProfile(profileId)),
            fallback: null as AgentProfileModelBindingRecord);
        return await ResolveConfigurationAsync(
            profileId,
            binding,
            forceProviderIdentityRefresh,
            canInvokeProvider,
            cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> IsCurrentConfigurationAsync(
        string profileId,
        ResolvedEmbeddingProvider expected,
        CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? canInvokeProvider = null)
    {
        var current = await ResolveConfigurationForProfileAsync(
            profileId,
            cancellationToken,
            forceProviderIdentityRefresh: true,
            canInvokeProvider).ConfigureAwait(false);
        return current is not null
               && current.IsSameActivation(expected)
               && string.Equals(current.ProviderId, expected.ProviderId, StringComparison.Ordinal)
               && string.Equals(current.ModelId, expected.ModelId, StringComparison.Ordinal)
               && string.Equals(
                   current.ConfigurationFingerprint,
                   expected.ConfigurationFingerprint,
                   StringComparison.Ordinal);
    }

    public async Task<SemanticEmbeddingContext> ResolveForSessionAsync(
        Guid sessionId,
        string? profileId = null,
        CancellationToken cancellationToken = default)
    {
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken).ConfigureAwait(false))
        {
            return SemanticEmbeddingContext.Disabled("Semantic retrieval is disabled in package settings.");
        }

        var sessionConfiguration = InvokeRuntimeCatalog(
            catalog =>
            {
                if (catalog.GetSession(sessionId) is null)
                {
                    return null;
                }

                var profile = string.IsNullOrWhiteSpace(profileId)
                    ? catalog.GetSessionProfile(sessionId)
                    : catalog.GetProfile(profileId);
                var binding = string.IsNullOrWhiteSpace(profileId)
                    ? catalog.GetSessionModelBinding(sessionId, AgentModelCapabilityKinds.Embedding)
                    : catalog.GetModelBinding(profileId, AgentModelCapabilityKinds.Embedding);
                return new SessionEmbeddingConfiguration(binding ?? ResolveLegacyBinding(profile));
            },
            fallback: null as SessionEmbeddingConfiguration);
        if (sessionConfiguration is null)
        {
            return SemanticEmbeddingContext.Unavailable("Session not found.");
        }

        var binding = sessionConfiguration.Binding;
        if (binding is null || string.IsNullOrWhiteSpace(binding.ProviderId) || string.IsNullOrWhiteSpace(binding.ModelId))
        {
            return SemanticEmbeddingContext.Disabled("No embedding provider/model is configured on this agent.");
        }

        var resolved = await ResolveConfigurationAsync(
            binding.ProfileId,
            binding,
            forceProviderIdentityRefresh: true,
            canInvokeProvider: null,
            cancellationToken).ConfigureAwait(false);
        if (resolved is null)
        {
            return SemanticEmbeddingContext.Unavailable($"Embedding provider '{binding.ProviderId}' is not installed.");
        }

        try
        {
            var readiness = await resolved.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
            return readiness.Status == AgentProviderReadinessStatus.Ready
                ? SemanticEmbeddingContext.Ready(
                    resolved.ProviderId,
                    resolved.ModelId,
                    resolved.ProviderDisplayName,
                    resolved.ConfigurationFingerprint)
                : SemanticEmbeddingContext.Unavailable(
                    $"Embedding provider '{resolved.ProviderDisplayName}' is not ready: {readiness.Message}");
        }
        catch (EmbeddingProviderRetiredException)
        {
            return SemanticEmbeddingContext.Unavailable($"Embedding provider '{resolved.ProviderDisplayName}' is no longer available.");
        }
    }

    internal AgentProfileModelBindingRecord? ResolveSessionBinding(Guid sessionId)
        => InvokeRuntimeCatalog(
            catalog => catalog.GetSessionModelBinding(sessionId, AgentModelCapabilityKinds.Embedding)
                       ?? ResolveLegacyBinding(catalog.GetSessionProfile(sessionId)),
            fallback: null as AgentProfileModelBindingRecord);

    internal TResult InvokeRuntimeCatalog<TResult>(
        Func<IAgentRuntimeCatalog, TResult> callback,
        TResult fallback)
    {
        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.RuntimeCatalogs))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            using (lease)
            {
                if (!lease.RetirementToken.IsCancellationRequested)
                {
                    return callback(lease.Contribution);
                }
            }
        }

        return fallback;
    }

    internal IDisposable SubscribeToSessionChanges(Action<Guid> handler)
        => SubscribeToRuntimeCatalog(
            catalog => catalog.SessionChanged += handler,
            catalog => catalog.SessionChanged -= handler);

    private IDisposable SubscribeToRuntimeCatalog(
        Action<IAgentRuntimeCatalog> subscribe,
        Action<IAgentRuntimeCatalog> unsubscribe)
    {
        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.RuntimeCatalogs))
        {
            if (reference.TryAcquire(out var lease))
            {
                return new RuntimeCatalogSubscription(lease, subscribe, unsubscribe);
            }
        }

        return EmptySubscription.Instance;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_catalogMonitor is not null)
        {
            _catalogMonitor.Changed -= OnExtensionCatalogChanged;
        }
        lock (_configurationCacheSync)
        {
            _configurationCache.Clear();
        }
        GC.SuppressFinalize(this);
    }

    private async Task<ResolvedEmbeddingProvider?> ResolveConfigurationAsync(
        string profileId,
        AgentProfileModelBindingRecord? binding,
        bool forceProviderIdentityRefresh,
        Func<CancellationToken, Task<bool>>? canInvokeProvider,
        CancellationToken cancellationToken)
    {
        if (binding is null
            || string.IsNullOrWhiteSpace(binding.ProviderId)
            || string.IsNullOrWhiteSpace(binding.ModelId))
        {
            return null;
        }

        var providerId = NormalizeProviderId(binding.ProviderId);
        var bindingFingerprint = BuildBindingFingerprint(binding, providerId);
        var catalogRevision = Volatile.Read(ref _catalogRevision);
        if (_catalogMonitor is not null && !forceProviderIdentityRefresh)
        {
            lock (_configurationCacheSync)
            {
                if (_configurationCache.TryGetValue(profileId, out var cached)
                    && cached.CatalogRevision == catalogRevision
                    && string.Equals(cached.BindingFingerprint, bindingFingerprint, StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - cached.CachedAtUtc < _configurationCacheDuration)
                {
                    return cached.Provider;
                }
            }
        }

        foreach (var reference in _invocationCatalog.GetExtensionReferences(PackageExtensionPoints.EmbeddingProviders))
        {
            if (!reference.TryAcquire(out var lease))
            {
                continue;
            }

            using (lease)
            {
                var retirementToken = lease.RetirementToken;
                if (retirementToken.IsCancellationRequested)
                {
                    continue;
                }
                if (!await CanInvokeProviderAsync(canInvokeProvider, cancellationToken).ConfigureAwait(false))
                {
                    return null;
                }
                var descriptor = lease.Contribution.Descriptor;
                if (!string.Equals(descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    retirementToken);
                string providerSpaceIdentity;
                try
                {
                    if (lease.Contribution is IAgentEmbeddingSpaceIdentityProvider
                        && !await CanInvokeProviderAsync(canInvokeProvider, cancellationToken).ConfigureAwait(false))
                    {
                        return null;
                    }
                    providerSpaceIdentity = lease.Contribution is IAgentEmbeddingSpaceIdentityProvider identityProvider
                        ? await identityProvider.GetEmbeddingSpaceIdentityAsync(binding.ModelId, invocation.Token).ConfigureAwait(false)
                        : string.Empty;
                }
                catch (Exception) when (retirementToken.IsCancellationRequested
                                        && !cancellationToken.IsCancellationRequested)
                {
                    continue;
                }
                var resolved = new ResolvedEmbeddingProvider(
                    reference,
                    lease.PackageId,
                    providerId,
                    binding.ModelId.Trim(),
                    string.IsNullOrWhiteSpace(descriptor.DisplayName) ? providerId : descriptor.DisplayName,
                    BuildConfigurationFingerprint(
                        binding,
                        lease.PackageId,
                        providerId,
                        lease.Contribution,
                        providerSpaceIdentity));
                if (_catalogMonitor is not null)
                {
                    lock (_configurationCacheSync)
                    {
                        _configurationCache[profileId] = new CachedEmbeddingConfiguration(
                            bindingFingerprint,
                            catalogRevision,
                            DateTimeOffset.UtcNow,
                            resolved);
                    }
                }
                return resolved;
            }
        }

        return null;
    }

    private static string BuildConfigurationFingerprint(
        AgentProfileModelBindingRecord binding,
        string ownerPackageId,
        string providerId,
        IAgentEmbeddingProvider provider,
        string? providerSpaceIdentity)
    {
        var providerType = provider.GetType();
        var source = string.Join('\n',
            "semantic-memory-embedding-space-v1",
            ownerPackageId.Trim().ToLowerInvariant(),
            providerId,
            binding.ModelId!.Trim(),
            binding.SettingsJson?.Trim() ?? string.Empty,
            providerSpaceIdentity?.Trim() ?? string.Empty,
            providerType.Assembly.FullName,
            providerType.FullName,
            providerType.Module.ModuleVersionId.ToString("D"));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private static string BuildBindingFingerprint(AgentProfileModelBindingRecord binding, string providerId)
    {
        var source = string.Join('\n',
            "semantic-memory-binding-v1",
            providerId,
            binding.ModelId!.Trim(),
            binding.SettingsJson?.Trim() ?? string.Empty);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source))).ToLowerInvariant();
    }

    private void OnExtensionCatalogChanged(object? sender, PackageExtensionCatalogChangedEventArgs e)
    {
        if (!e.IncludesExtensionPoint(PackageExtensionPoints.EmbeddingProviders.Id)
            && !e.IncludesExtensionPoint(PackageExtensionPoints.RuntimeCatalogs.Id))
        {
            return;
        }

        Volatile.Write(ref _catalogRevision, e.Revision);
        lock (_configurationCacheSync)
        {
            _configurationCache.Clear();
        }
    }

    internal static string NormalizeProviderId(string providerId) => providerId.Trim().ToLowerInvariant();

    private static Task<bool> CanInvokeProviderAsync(
        Func<CancellationToken, Task<bool>>? canInvokeProvider,
        CancellationToken cancellationToken)
        => canInvokeProvider?.Invoke(cancellationToken) ?? Task.FromResult(true);

    internal static AgentProfileModelBindingRecord? ResolveLegacyBinding(AgentProfileRecord? profile)
        => profile is null
           || string.IsNullOrWhiteSpace(profile.EmbeddingProviderId)
           || string.IsNullOrWhiteSpace(profile.EmbeddingModelId)
            ? null
            : new AgentProfileModelBindingRecord(
                profile.ProfileId,
                AgentModelCapabilityKinds.Embedding,
                profile.EmbeddingProviderId,
                profile.EmbeddingModelId,
                SettingsJson: null,
                profile.UpdatedAtUtc);

    private sealed record SessionEmbeddingConfiguration(AgentProfileModelBindingRecord? Binding);

    private sealed record CachedEmbeddingConfiguration(
        string BindingFingerprint,
        long CatalogRevision,
        DateTimeOffset CachedAtUtc,
        ResolvedEmbeddingProvider Provider);

    private sealed class RuntimeCatalogSubscription : IDisposable
    {
        private readonly Action<IAgentRuntimeCatalog> _unsubscribe;
        private IPackageExtensionLease<IAgentRuntimeCatalog>? _lease;
        private CancellationTokenRegistration _retirementRegistration;
        private int _disposed;

        public RuntimeCatalogSubscription(
            IPackageExtensionLease<IAgentRuntimeCatalog> lease,
            Action<IAgentRuntimeCatalog> subscribe,
            Action<IAgentRuntimeCatalog> unsubscribe)
        {
            _lease = lease;
            _unsubscribe = unsubscribe;
            subscribe(lease.Contribution);
            _retirementRegistration = lease.RetirementToken.UnsafeRegister(
                static state => ((RuntimeCatalogSubscription)state!).DisposeFromRetirement(),
                this);
            if (Volatile.Read(ref _disposed) != 0)
            {
                _retirementRegistration.Dispose();
            }
        }

        public void Dispose() => DisposeCore(disposeRegistration: true);

        private void DisposeFromRetirement() => DisposeCore(disposeRegistration: false);

        private void DisposeCore(bool disposeRegistration)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
            {
                return;
            }

            var lease = Interlocked.Exchange(ref _lease, null);
            if (lease is not null)
            {
                try
                {
                    _unsubscribe(lease.Contribution);
                }
                finally
                {
                    lease.Dispose();
                }
            }
            if (disposeRegistration)
            {
                _retirementRegistration.Dispose();
            }
        }
    }

    private sealed class EmptySubscription : IDisposable
    {
        public static EmptySubscription Instance { get; } = new();

        public void Dispose()
        {
        }
    }
}

public sealed class ResolvedEmbeddingProvider
{
    private readonly IPackageExtensionReference<IAgentEmbeddingProvider> _reference;

    internal ResolvedEmbeddingProvider(
        IPackageExtensionReference<IAgentEmbeddingProvider> reference,
        string ownerPackageId,
        string providerId,
        string modelId,
        string providerDisplayName,
        string configurationFingerprint)
    {
        _reference = reference;
        OwnerPackageId = ownerPackageId;
        ProviderId = providerId;
        ModelId = modelId;
        ProviderDisplayName = providerDisplayName;
        ConfigurationFingerprint = configurationFingerprint;
    }

    public string OwnerPackageId { get; }

    public string ProviderId { get; }

    public string ModelId { get; }

    public string ProviderDisplayName { get; }

    public string ConfigurationFingerprint { get; }

    internal ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken)
        => InvokeAsync(
            static (provider, _, token) => provider.GetReadinessAsync(token),
            state: 0,
            cancellationToken);

    internal ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
        string text,
        CancellationToken cancellationToken)
        => InvokeAsync(
            static (provider, state, token) => provider.GenerateEmbeddingAsync(state.ModelId, state.Text, token),
            (ModelId, Text: text),
            cancellationToken);

    internal ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
        IReadOnlyList<string> texts,
        CancellationToken cancellationToken)
        => InvokeAsync(
            static (provider, state, token) => provider.GenerateEmbeddingsAsync(state.ModelId, state.Texts, token),
            (ModelId, Texts: texts),
            cancellationToken);

    internal bool IsSameActivation(ResolvedEmbeddingProvider other)
    {
        if (!_reference.TryAcquire(out var leftLease))
        {
            return false;
        }
        using (leftLease)
        {
            if (!other._reference.TryAcquire(out var rightLease))
            {
                return false;
            }
            using (rightLease)
            {
                return !leftLease.RetirementToken.IsCancellationRequested
                       && !rightLease.RetirementToken.IsCancellationRequested
                       && ReferenceEquals(leftLease.Contribution, rightLease.Contribution);
            }
        }
    }

    private async ValueTask<TResult> InvokeAsync<TState, TResult>(
        Func<IAgentEmbeddingProvider, TState, CancellationToken, ValueTask<TResult>> callback,
        TState state,
        CancellationToken cancellationToken)
    {
        if (!_reference.TryAcquire(out var lease))
        {
            throw new EmbeddingProviderRetiredException(ProviderId);
        }

        using (lease)
        {
            var retirementToken = lease.RetirementToken;
            if (retirementToken.IsCancellationRequested)
            {
                throw new EmbeddingProviderRetiredException(ProviderId);
            }

            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                retirementToken);
            try
            {
                return await callback(lease.Contribution, state, invocation.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (retirementToken.IsCancellationRequested
                                       && !cancellationToken.IsCancellationRequested)
            {
                throw new EmbeddingProviderRetiredException(ProviderId, ex);
            }
        }
    }
}

internal sealed class EmbeddingProviderRetiredException : InvalidOperationException
{
    public EmbeddingProviderRetiredException(string providerId, Exception? innerException = null)
        : base($"Embedding provider '{providerId}' retired during invocation.", innerException)
    {
    }
}

public sealed record SemanticEmbeddingContext(
    string StatusLabel,
    string StatusText,
    bool IsReady,
    string? ProviderId = null,
    string? ModelId = null,
    string? ProviderDisplayName = null,
    string? ConfigurationFingerprint = null)
{
    public static SemanticEmbeddingContext Ready(
        string providerId,
        string modelId,
        string providerDisplayName,
        string configurationFingerprint = "")
        => new(
            "Ready",
            $"Semantic retrieval is active via {providerDisplayName} / {modelId}.",
            true,
            providerId,
            modelId,
            providerDisplayName,
            configurationFingerprint);

    public static SemanticEmbeddingContext Disabled(string statusText) => new("Disabled", statusText, false);

    public static SemanticEmbeddingContext Unavailable(string statusText) => new("Unavailable", statusText, false);
}
