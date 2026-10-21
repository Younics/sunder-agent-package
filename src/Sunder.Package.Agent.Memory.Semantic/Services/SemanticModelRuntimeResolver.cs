using System.Security.Cryptography;
using System.Text;
using IEmbeddingProviderService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Embedding_Provider.IEmbeddingProviderService;
using IRuntimeCatalogService = Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Memory.Semantic.Services;

public sealed class SemanticModelRuntimeResolver : IDisposable, IAsyncDisposable, ISunderWorkerLifecycleService
{
    private static readonly TimeSpan DefaultConfigurationCacheDuration = TimeSpan.FromMinutes(5);
    private readonly IServiceProviders<IRuntimeCatalogService> _catalogs;
    private readonly IServiceProviders<IEmbeddingProviderService> _embeddings;
    private readonly MemorySemanticSettingsService _settingsService;
    private readonly TimeSpan _configurationCacheDuration;
    private readonly object _gate = new();
    private readonly Dictionary<string, CachedEmbeddingConfiguration> _configurationCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<RuntimeCatalogSubscription> _subscriptions = [];
    private readonly CancellationTokenSource _lifetime = new();
    private Task _completion = Task.CompletedTask;
    private Task? _stop;
    private long _catalogRevision;
    private bool _started;
    private bool _disposed;

    public SemanticModelRuntimeResolver(IServiceProviders<IRuntimeCatalogService> catalogs, IServiceProviders<IEmbeddingProviderService> embeddings,
        MemorySemanticSettingsService settingsService, TimeSpan? configurationCacheDuration = null)
    {
        _catalogs = catalogs; _embeddings = embeddings; _settingsService = settingsService;
        _configurationCacheDuration = configurationCacheDuration ?? DefaultConfigurationCacheDuration;
        if (_configurationCacheDuration <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(configurationCacheDuration));
    }

    public Task Completion { get { lock (_gate) return _completion; } }
    public ValueTask PrepareCandidateAsync(CancellationToken token = default) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask CommitGenerationAsync(SunderWorkerGenerationContext generation, CancellationToken token = default) { token.ThrowIfCancellationRequested(); return ValueTask.CompletedTask; }
    public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_started)
            {
                _started = true;
                _completion = Task.WhenAll(Task.Run(() => WatchProvidersAsync(_catalogs)), Task.Run(() => WatchProvidersAsync(_embeddings)));
            }
        }
        return ValueTask.CompletedTask;
    }
    public ValueTask ShutdownAsync(SunderWorkerShutdownContext context, CancellationToken token = default) => DisposeAsync();

    public Task<ResolvedEmbeddingProvider?> ResolveForProfileAsync(string profileId, CancellationToken cancellationToken = default)
        => ResolveForProfileCoreAsync(profileId, null, cancellationToken);
    internal Task<ResolvedEmbeddingProvider?> ResolveForProfileAsync(string profileId, Func<CancellationToken, Task<bool>> canInvokeProvider, CancellationToken cancellationToken)
        => ResolveForProfileCoreAsync(profileId, canInvokeProvider, cancellationToken);

    private async Task<ResolvedEmbeddingProvider?> ResolveForProfileCoreAsync(string profileId, Func<CancellationToken, Task<bool>>? canInvokeProvider, CancellationToken token)
    {
        var resolved = await ResolveConfigurationForProfileAsync(profileId, token, true, canInvokeProvider).ConfigureAwait(false);
        if (resolved is null || !await CanInvokeProviderAsync(canInvokeProvider, token).ConfigureAwait(false)) return null;
        try { return (await resolved.GetReadinessAsync(token).ConfigureAwait(false)).Status == AgentProviderReadinessStatus.Ready ? resolved : null; }
        catch (EmbeddingProviderRetiredException) { return null; }
    }

    internal async Task<ResolvedEmbeddingProvider?> ResolveConfigurationForProfileAsync(string profileId, CancellationToken cancellationToken = default,
        bool forceProviderIdentityRefresh = false, Func<CancellationToken, Task<bool>>? canInvokeProvider = null)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return null;
        var binding = await InvokeRuntimeCatalogAsync(async (catalog, token) =>
            await catalog.GetModelBindingAsync(new() { ProfileId = profileId, CapabilityKind = AgentModelCapabilityKinds.Embedding }, token).ConfigureAwait(false)
                ?? ResolveProfileEmbeddingBinding(await catalog.GetProfileAsync(profileId, token).ConfigureAwait(false)),
            null as AgentProfileModelBindingRecord, cancellationToken).ConfigureAwait(false);
        return await ResolveConfigurationAsync(profileId, binding, forceProviderIdentityRefresh, canInvokeProvider, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<bool> IsCurrentConfigurationAsync(string profileId, ResolvedEmbeddingProvider expected, CancellationToken cancellationToken = default,
        Func<CancellationToken, Task<bool>>? canInvokeProvider = null)
    {
        var current = await ResolveConfigurationForProfileAsync(profileId, cancellationToken, true, canInvokeProvider).ConfigureAwait(false);
        return current is not null && current.IsSameActivation(expected)
            && string.Equals(current.ProviderId, expected.ProviderId, StringComparison.Ordinal)
            && string.Equals(current.ModelId, expected.ModelId, StringComparison.Ordinal)
            && string.Equals(current.ConfigurationFingerprint, expected.ConfigurationFingerprint, StringComparison.Ordinal);
    }

    public async Task<SemanticEmbeddingContext> ResolveForSessionAsync(Guid sessionId, string? profileId = null, CancellationToken cancellationToken = default)
    {
        if (!await _settingsService.IsSemanticRetrievalEnabledAsync(cancellationToken).ConfigureAwait(false))
            return SemanticEmbeddingContext.Disabled("Semantic retrieval is disabled in package settings.");
        var configuration = await InvokeRuntimeCatalogAsync(async (catalog, token) =>
        {
            if (await catalog.GetSessionAsync(sessionId, token).ConfigureAwait(false) is null) return null;
            var profile = string.IsNullOrWhiteSpace(profileId)
                ? await catalog.GetSessionProfileAsync(sessionId, token).ConfigureAwait(false)
                : await catalog.GetProfileAsync(profileId, token).ConfigureAwait(false);
            var binding = string.IsNullOrWhiteSpace(profileId)
                ? await catalog.GetSessionModelBindingAsync(new() { SessionId = sessionId, CapabilityKind = AgentModelCapabilityKinds.Embedding }, token).ConfigureAwait(false)
                : await catalog.GetModelBindingAsync(new() { ProfileId = profileId, CapabilityKind = AgentModelCapabilityKinds.Embedding }, token).ConfigureAwait(false);
            return new SessionEmbeddingConfiguration(binding ?? ResolveProfileEmbeddingBinding(profile));
        }, null as SessionEmbeddingConfiguration, cancellationToken).ConfigureAwait(false);
        if (configuration is null) return SemanticEmbeddingContext.Unavailable("Session not found.");
        var binding = configuration.Binding;
        if (binding is null || string.IsNullOrWhiteSpace(binding.ProviderId) || string.IsNullOrWhiteSpace(binding.ModelId))
            return SemanticEmbeddingContext.Disabled("No embedding provider/model is configured on this agent.");
        var resolved = await ResolveConfigurationAsync(binding.ProfileId, binding, true, null, cancellationToken).ConfigureAwait(false);
        if (resolved is null) return SemanticEmbeddingContext.Unavailable($"Embedding provider '{binding.ProviderId}' is not installed.");
        try
        {
            var readiness = await resolved.GetReadinessAsync(cancellationToken).ConfigureAwait(false);
            return readiness.Status == AgentProviderReadinessStatus.Ready
                ? SemanticEmbeddingContext.Ready(resolved.ProviderId, resolved.ModelId, resolved.ProviderDisplayName, resolved.ConfigurationFingerprint)
                : SemanticEmbeddingContext.Unavailable($"Embedding provider '{resolved.ProviderDisplayName}' is not ready: {readiness.Message}");
        }
        catch (EmbeddingProviderRetiredException) { return SemanticEmbeddingContext.Unavailable($"Embedding provider '{resolved.ProviderDisplayName}' is no longer available."); }
    }

    internal TResult InvokeRuntimeCatalog<TResult>(Func<IRuntimeCatalogService, CancellationToken, ValueTask<TResult>> callback, TResult fallback)
        => InvokeRuntimeCatalogAsync(callback, fallback, CancellationToken.None).AsTask().GetAwaiter().GetResult();

    internal async ValueTask<TResult> InvokeRuntimeCatalogAsync<TResult>(Func<IRuntimeCatalogService, CancellationToken, ValueTask<TResult>> callback,
        TResult fallback, CancellationToken token)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token, _lifetime.Token);
        var snapshot = await _catalogs.ListAsync(lifetime.Token).ConfigureAwait(false);
        foreach (var provider in snapshot.Providers.Where(value => value.State == ServiceProviderState.Active))
        {
            if (provider.RetirementToken.IsCancellationRequested) continue;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, provider.RetirementToken);
            try { await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false); }
            catch (SunderRpcException error) when (error.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable) { continue; }
            var result = await callback(provider.Service, invocation.Token).ConfigureAwait(false);
            await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            return result;
        }
        return fallback;
    }

    internal IDisposable SubscribeToSessionChanges(Action<Guid> handler, Action resetHandler)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var completed in _subscriptions.Where(value => value.Completion.IsCompletedSuccessfully).ToArray())
            { completed.Release(); _subscriptions.Remove(completed); }
            var subscription = new RuntimeCatalogSubscription(_catalogs, handler, resetHandler, _lifetime.Token);
            _subscriptions.Add(subscription);
            return subscription;
        }
    }

    private async Task<ResolvedEmbeddingProvider?> ResolveConfigurationAsync(string profileId, AgentProfileModelBindingRecord? binding,
        bool forceProviderIdentityRefresh, Func<CancellationToken, Task<bool>>? canInvokeProvider, CancellationToken cancellationToken)
    {
        if (binding is null || string.IsNullOrWhiteSpace(binding.ProviderId) || string.IsNullOrWhiteSpace(binding.ModelId)) return null;
        var providerId = NormalizeProviderId(binding.ProviderId);
        var fingerprint = BuildBindingFingerprint(binding, providerId);
        var revision = Volatile.Read(ref _catalogRevision);
        if (!forceProviderIdentityRefresh)
        {
            lock (_gate)
                if (_configurationCache.TryGetValue(profileId, out var cached) && cached.CatalogRevision == revision
                    && !cached.Provider.IsRetired && string.Equals(cached.BindingFingerprint, fingerprint, StringComparison.Ordinal)
                    && DateTimeOffset.UtcNow - cached.CachedAtUtc < _configurationCacheDuration) return cached.Provider;
        }
        var snapshot = await _embeddings.ListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var provider in snapshot.Providers.Where(value => value.State == ServiceProviderState.Active))
        {
            var retirement = provider.RetirementToken;
            if (retirement.IsCancellationRequested) continue;
            if (!await CanInvokeProviderAsync(canInvokeProvider, cancellationToken).ConfigureAwait(false)) return null;
            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, retirement, _lifetime.Token);
            try
            {
                await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
                var descriptor = await provider.Service.DescribeAsync(invocation.Token).ConfigureAwait(false);
                if (!string.Equals(descriptor.ProviderId, providerId, StringComparison.OrdinalIgnoreCase)) continue;
                if (!await CanInvokeProviderAsync(canInvokeProvider, cancellationToken).ConfigureAwait(false)) return null;
                var space = await provider.Service.SpaceIdentityAsync(binding.ModelId, invocation.Token).ConfigureAwait(false);
                await provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
                var resolved = new ResolvedEmbeddingProvider(provider, providerId, binding.ModelId.Trim(),
                    string.IsNullOrWhiteSpace(descriptor.DisplayName) ? providerId : descriptor.DisplayName,
                    BuildConfigurationFingerprint(binding, provider, providerId, space));
                lock (_gate) _configurationCache[profileId] = new(fingerprint, revision, DateTimeOffset.UtcNow, resolved);
                return resolved;
            }
            catch (Exception error) when (Retired(error, retirement, cancellationToken)) { }
        }
        return null;
    }

    private async Task WatchProvidersAsync<T>(IServiceProviders<T> providers) where T : class
    {
        long revision = 0, sequence = 0;
        while (!_lifetime.IsCancellationRequested)
        {
            try
            {
                await foreach (var change in providers.WatchAsync(revision, sequence, _lifetime.Token).ConfigureAwait(false))
                {
                    revision = change.Revision; sequence = change.Sequence;
                    if (change.Kind == ServiceProviderChangeKind.ResetRequired)
                    {
                        var snapshot = await providers.ListAsync(_lifetime.Token).ConfigureAwait(false);
                        revision = snapshot.Revision; sequence = snapshot.Sequence;
                    }
                    OnCatalogChanged();
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
            catch (Exception error) when (error is SunderRpcException or OperationCanceledException) { OnCatalogChanged(); }
            try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
        }
    }

    private void OnCatalogChanged() { Interlocked.Increment(ref _catalogRevision); lock (_gate) _configurationCache.Clear(); }
    internal static bool Retired(Exception error, CancellationToken retirement, CancellationToken caller)
        => !caller.IsCancellationRequested && (error is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable }
            || retirement.IsCancellationRequested && error is OperationCanceledException or SunderRpcException { Error.Kind: SunderRpcErrorKind.Cancelled });
    internal static string NormalizeProviderId(string providerId) => providerId.Trim().ToLowerInvariant();
    private static Task<bool> CanInvokeProviderAsync(Func<CancellationToken, Task<bool>>? callback, CancellationToken token) => callback?.Invoke(token) ?? Task.FromResult(true);

    internal static AgentProfileModelBindingRecord? ResolveProfileEmbeddingBinding(AgentProfileRecord? profile)
        => profile is null || string.IsNullOrWhiteSpace(profile.EmbeddingProviderId) || string.IsNullOrWhiteSpace(profile.EmbeddingModelId) ? null : new()
        {
            ProfileId = profile.ProfileId, CapabilityKind = AgentModelCapabilityKinds.Embedding,
            ProviderId = profile.EmbeddingProviderId, ModelId = profile.EmbeddingModelId, SettingsJson = null, UpdatedAtUtc = profile.UpdatedAtUtc,
        };
    private static string BuildConfigurationFingerprint(AgentProfileModelBindingRecord binding, ServiceProvider<IEmbeddingProviderService> provider, string providerId, string? space)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "semantic-memory-embedding-space-v2",
            provider.PackageId.Trim().ToLowerInvariant(), providerId, binding.ModelId!.Trim(), binding.SettingsJson?.Trim() ?? string.Empty,
            space?.Trim() ?? string.Empty, provider.PackageVersion, provider.ContractVersion, provider.DescriptorSha256, provider.AbiSha256, provider.ProviderId))));
    private static string BuildBindingFingerprint(AgentProfileModelBindingRecord binding, string providerId)
        => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', "semantic-memory-binding-v1", providerId,
            binding.ModelId!.Trim(), binding.SettingsJson?.Trim() ?? string.Empty))));

    public void Dispose()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _configurationCache.Clear(); }
        _lifetime.Cancel();
    }
    public ValueTask DisposeAsync()
    {
        Dispose();
        lock (_gate) return new(_stop ??= StopAsync());
    }
    private async Task StopAsync()
    {
        RuntimeCatalogSubscription[] subscriptions;
        lock (_gate) subscriptions = _subscriptions.ToArray();
        try { await Task.WhenAll(new[] { _completion }.Concat(subscriptions.Select(value => value.Completion))).ConfigureAwait(false); }
        finally { foreach (var subscription in subscriptions) subscription.Release(); _lifetime.Dispose(); }
    }

    private sealed record SessionEmbeddingConfiguration(AgentProfileModelBindingRecord? Binding);
    private sealed record CachedEmbeddingConfiguration(string BindingFingerprint, long CatalogRevision, DateTimeOffset CachedAtUtc, ResolvedEmbeddingProvider Provider);

    private sealed class RuntimeCatalogSubscription : IDisposable
    {
        private readonly CancellationTokenSource _lifetime;
        private int _disposed;
        internal RuntimeCatalogSubscription(IServiceProviders<IRuntimeCatalogService> catalogs, Action<Guid> handler, Action reset, CancellationToken lifetime)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
            Completion = Task.Run(async () =>
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    try
                    {
                        var snapshot = await catalogs.ListAsync(_lifetime.Token).ConfigureAwait(false);
                        var provider = snapshot.Providers.FirstOrDefault(value => value.State == ServiceProviderState.Active);
                        if (provider is not null)
                        {
                            using var invocation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token, provider.RetirementToken);
                            long expected = 1;
                            await foreach (var change in provider.Service.WatchAsync(invocation.Token).ConfigureAwait(false))
                            {
                                var sequence = change switch
                                {
                                    AgentRuntimeCatalogResetEvent value => value.Sequence,
                                    AgentSessionCatalogEvent value => value.Sequence,
                                    AgentTurnCatalogEvent value => value.Sequence,
                                    AgentProfileCatalogEvent value => value.Sequence,
                                    _ => throw new InvalidDataException("Unknown Runtime catalog event."),
                                };
                                if (sequence != expected && change is not AgentRuntimeCatalogResetEvent) reset();
                                expected = sequence + 1;
                                if (change is AgentRuntimeCatalogResetEvent) reset();
                                else if (change is AgentSessionCatalogEvent session) handler(session.SessionId);
                            }
                        }
                        reset();
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                    catch (Exception error) when (error is OperationCanceledException or SunderRpcException) { reset(); }
                    try { await Task.Delay(250, _lifetime.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return; }
                }
            });
        }
        internal Task Completion { get; }
        public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) _lifetime.Cancel(); }
        internal void Release() { Interlocked.Exchange(ref _disposed, 1); _lifetime.Dispose(); }
    }
}

public sealed class ResolvedEmbeddingProvider
{
    private readonly ServiceProvider<IEmbeddingProviderService> _provider;
    internal ResolvedEmbeddingProvider(ServiceProvider<IEmbeddingProviderService> provider, string providerId, string modelId, string displayName, string fingerprint)
    { _provider = provider; ProviderId = providerId; ModelId = modelId; ProviderDisplayName = displayName; ConfigurationFingerprint = fingerprint; }
    public string OwnerPackageId => _provider.PackageId;
    public string ProviderId { get; }
    public string ModelId { get; }
    public string ProviderDisplayName { get; }
    public string ConfigurationFingerprint { get; }
    internal bool IsRetired => _provider.RetirementToken.IsCancellationRequested;
    internal ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken token)
        => InvokeAsync((service, cancellation) => service.GetReadinessAsync(cancellation), token);
    internal ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(string text, CancellationToken token)
        => InvokeAsync((service, cancellation) => service.GenerateAsync(new() { ModelId = ModelId, Text = text }, cancellation), token);
    internal ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(IReadOnlyList<string> texts, CancellationToken token)
        => InvokeAsync((service, cancellation) => service.GenerateBatchAsync(new() { ModelId = ModelId, Texts = texts }, cancellation), token);
    internal bool IsSameActivation(ResolvedEmbeddingProvider other)
        => !IsRetired && !other.IsRetired && _provider.RetirementToken == other._provider.RetirementToken;
    private async ValueTask<T> InvokeAsync<T>(Func<IEmbeddingProviderService, CancellationToken, ValueTask<T>> invoke, CancellationToken caller)
    {
        if (IsRetired) throw new EmbeddingProviderRetiredException(ProviderId);
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(caller, _provider.RetirementToken);
        try
        {
            await _provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            var result = await invoke(_provider.Service, invocation.Token).ConfigureAwait(false);
            await _provider.EnsureCurrentAsync(invocation.Token).ConfigureAwait(false);
            return result;
        }
        catch (Exception error) when (SemanticModelRuntimeResolver.Retired(error, _provider.RetirementToken, caller))
        { throw new EmbeddingProviderRetiredException(ProviderId, error); }
    }
}

internal sealed class EmbeddingProviderRetiredException(string providerId, Exception? innerException = null)
    : InvalidOperationException($"Embedding provider '{providerId}' retired during invocation.", innerException);
