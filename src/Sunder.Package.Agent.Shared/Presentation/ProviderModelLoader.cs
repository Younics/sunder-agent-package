using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Shared.Presentation;

internal sealed record ProviderCatalogOption(string? Id, string Label, string? PackageId = null)
{
    public string? ProviderId => Id;
}

internal sealed record ProviderModelCatalogOption(
    string Id,
    string Label,
    IReadOnlyList<AgentModelVariantDescriptor>? Variants = null,
    IReadOnlyList<AgentModelSpeedOptionDescriptor>? SpeedOptions = null,
    IReadOnlyList<AgentModelModeOptionDescriptor>? ModeOptions = null)
{
    public string ModelId => Id;
}

internal sealed record ProviderModelCatalogResult(
    IReadOnlyList<ProviderModelCatalogOption> Models,
    string StatusText,
    string? WarningText = null);

internal interface IProviderModelCatalog
{
    IReadOnlyList<ProviderCatalogOption> ListProviders();

    Task<ProviderModelCatalogResult> LoadAsync(
        string providerId,
        CancellationToken cancellationToken);
}

internal sealed class ProviderModelCatalogAdapter(
    Func<IReadOnlyList<ProviderCatalogOption>> listProviders,
    Func<string, CancellationToken, Task<ProviderModelCatalogResult>> load) : IProviderModelCatalog
{
    private readonly Func<IReadOnlyList<ProviderCatalogOption>> _listProviders = listProviders;
    private readonly Func<string, CancellationToken, Task<ProviderModelCatalogResult>> _load = load;

    public IReadOnlyList<ProviderCatalogOption> ListProviders() => _listProviders();

    public Task<ProviderModelCatalogResult> LoadAsync(
        string providerId,
        CancellationToken cancellationToken) => _load(providerId, cancellationToken);

    public static IProviderModelCatalog ForChatProviders(IPackageExtensionCatalog extensionCatalog)
        => new ProviderModelCatalogAdapter(
            () => extensionCatalog
                .GetExtensions(PackageExtensionPoints.ChatProviders)
                .OrderBy(provider => provider.Descriptor.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Select(provider => new ProviderCatalogOption(
                    provider.Descriptor.ProviderId,
                    provider.Descriptor.DisplayName,
                    provider.Descriptor.PackageId))
                .ToArray(),
            async (providerId, cancellationToken) =>
            {
                var provider = extensionCatalog
                    .GetExtensions(PackageExtensionPoints.ChatProviders)
                    .FirstOrDefault(candidate => string.Equals(
                        candidate.Descriptor.ProviderId,
                        providerId,
                        StringComparison.OrdinalIgnoreCase));
                if (provider is null)
                {
                    return new ProviderModelCatalogResult([], "Chat provider is no longer installed.");
                }

                var modelsTask = provider.GetAvailableModelsAsync(cancellationToken).AsTask();
                var readinessTask = provider.GetReadinessAsync(cancellationToken).AsTask();
                await Task.WhenAll(modelsTask, readinessTask).ConfigureAwait(false);
                var readiness = await readinessTask.ConfigureAwait(false);
                return new ProviderModelCatalogResult(
                    (await modelsTask.ConfigureAwait(false))
                        .OrderNewestFirst()
                        .Select(ToCatalogOption)
                        .ToArray(),
                    $"Chat provider status: {readiness.Status} - {readiness.Message}",
                    readiness.Status == AgentProviderReadinessStatus.Ready
                        ? null
                        : readiness.Message);
            });

    public static ProviderModelCatalogOption ToCatalogOption(AgentModelDescriptor model)
        => new(
            model.ModelId,
            model.DisplayName,
            model.Variants,
            model.SpeedOptions,
            model.ModeOptions);

    public static ProviderModelCatalogOption ToCatalogOption(AgentEmbeddingModelDescriptor model)
        => new(model.ModelId, model.DisplayName);
}

internal sealed class ProviderModelLoader(IProviderModelCatalog catalog) : IDisposable
{
    private const int MaximumConcurrentLoads = 2;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadGate = new(MaximumConcurrentLoads, MaximumConcurrentLoads);
    private readonly IProviderModelCatalog _catalog = catalog;
    private CancellationTokenSource? _loadCancellation;
    private int _generation;
    private bool _disposed;

    public IReadOnlyList<ProviderCatalogOption> ListProviders() => _catalog.ListProviders();

    public async Task<ProviderModelCatalogResult?> LoadAsync(
        string providerId,
        CancellationToken cancellationToken = default)
    {
        CancellationTokenSource loadCancellation;
        CancellationTokenSource? supersededCancellation;
        int generation;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            generation = ++_generation;
            supersededCancellation = _loadCancellation;
            loadCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _loadCancellation = loadCancellation;
        }

        SafeCancel(supersededCancellation);
        var enteredLoadGate = false;
        try
        {
            await _loadGate.WaitAsync(loadCancellation.Token).ConfigureAwait(false);
            enteredLoadGate = true;

            // Catalog implementations can perform synchronous configuration and secret reads
            // before returning their task. Keep that entire invocation off the UI thread.
            var result = await Task.Run(
                    () => _catalog.LoadAsync(providerId, loadCancellation.Token),
                    loadCancellation.Token)
                .ConfigureAwait(false);

            lock (_gate)
            {
                return !_disposed && generation == _generation ? result : null;
            }
        }
        catch (OperationCanceledException) when (loadCancellation.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            if (enteredLoadGate)
            {
                _loadGate.Release();
            }

            lock (_gate)
            {
                if (ReferenceEquals(_loadCancellation, loadCancellation))
                {
                    _loadCancellation = null;
                }
            }

            loadCancellation.Dispose();
        }
    }

    public void Cancel()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            _generation++;
            cancellation = _loadCancellation;
            _loadCancellation = null;
        }

        SafeCancel(cancellation);
    }

    public void Dispose()
    {
        CancellationTokenSource? cancellation;
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _generation++;
            cancellation = _loadCancellation;
            _loadCancellation = null;
        }

        SafeCancel(cancellation);
    }

    private static void SafeCancel(CancellationTokenSource? cancellation)
    {
        try
        {
            cancellation?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The superseded operation completed between releasing the gate and cancellation.
        }
    }
}
