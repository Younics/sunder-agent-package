using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Runtime;

internal sealed class AgentWorkerLifecycle : ISunderWorkerLifecycleService
{
    private static readonly TimeSpan DefaultCleanupTimeout = TimeSpan.FromSeconds(5);
    private readonly IServiceProvider? _services;
    private readonly Func<AgentRpcCatalog> _catalog;
    private readonly Func<AgentRuntimeStartupService> _createStartup;
    private readonly IReadOnlyList<DeferredAgentRpcServiceHandler> _providers;
    private readonly TimeSpan _cleanupTimeout;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TaskCompletionSource _completion = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private PackageRuntimeGeneration? _generation;
    private AgentRuntimeStartupService? _startup;
    private bool _active;
    private int _catalogDisposed;
    private int _generationFaultObserved;
    private int _stopping;

    [ActivatorUtilitiesConstructor]
    internal AgentWorkerLifecycle(AgentRpcCatalog catalog, Lazy<AgentRuntimeStartupService> startup)
    {
        _catalog = () => catalog;
        _createStartup = () => startup.Value;
        _providers = [];
        _cleanupTimeout = DefaultCleanupTimeout;
    }

    internal AgentWorkerLifecycle(
        IServiceProvider services,
        IReadOnlyList<DeferredAgentRpcServiceHandler> providers,
        TimeSpan? cleanupTimeout = null)
    {
        _services = services;
        _catalog = services.GetRequiredService<AgentRpcCatalog>;
        _createStartup = services.GetRequiredService<AgentRuntimeStartupService>;
        _providers = providers;
        _cleanupTimeout = cleanupTimeout ?? DefaultCleanupTimeout;
        if (_cleanupTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cleanupTimeout),
                "Agent Worker cleanup timeout must be positive.");
        }
    }

    public Task Completion => _completion.Task;

    public ValueTask PrepareCandidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }

    public ValueTask CommitGenerationAsync(
        SunderWorkerGenerationContext generation,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CommitGeneration(new PackageRuntimeGeneration(
            generation.ActivationId,
            generation.SessionGeneration));
        return ValueTask.CompletedTask;
    }

    internal void CommitGeneration(PackageRuntimeGeneration committed)
    {
        lock (_providers)
        {
            if (_generation is not null && _generation != committed)
            {
                throw new InvalidOperationException(
                    "The Agent Worker is already committed to another Runtime generation.");
            }
            _generation ??= committed;
        }
    }

    public async ValueTask ActivateAsync(
        SunderWorkerContext context,
        CancellationToken cancellationToken = default)
        => await ActivateAsync(context.ReportGenerationFaultAsync, cancellationToken).ConfigureAwait(false);

    internal async ValueTask ActivateAsync(
        Func<string, string, CancellationToken, ValueTask> reportGenerationFault,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_active) return;
            if (IsStopping)
            {
                throw new InvalidOperationException("The Agent Worker is already stopping.");
            }
            var generation = _generation
                ?? throw new InvalidOperationException(
                    "The Agent Worker must receive an exact Runtime generation before activation.");

            var catalog = _catalog();
            var startup = _createStartup();
            try
            {
                await startup.StartAsync(cancellationToken).ConfigureAwait(false);
                await startup.CommitGenerationAsync(generation, cancellationToken).ConfigureAwait(false);
                catalog.StartWatching();
                foreach (var provider in _providers) provider.Activate(_services!);
            }
            catch
            {
                DeactivateProviders();
                _ = DisposeCatalog();
                _ = await StopStartupBoundedAsync(startup).ConfigureAwait(false);
                throw;
            }

            _startup = startup;
            _active = true;
            _ = MonitorGenerationAsync(startup, reportGenerationFault);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsStopping) return;
            Volatile.Write(ref _stopping, 1);
            DeactivateProviders();
            Exception? failure = DisposeCatalog();
            if (_startup is not null)
            {
                failure = await StopStartupBoundedAsync(_startup).ConfigureAwait(false)
                          ?? failure;
            }
            _active = false;
            if (Volatile.Read(ref _generationFaultObserved) == 0)
            {
                _completion.TrySetResult();
            }
            if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
        }
        finally
        {
            _gate.Release();
        }

    }

    public async ValueTask ShutdownAsync(
        SunderWorkerShutdownContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        await StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task MonitorGenerationAsync(
        AgentRuntimeStartupService startup,
        Func<string, string, CancellationToken, ValueTask> reportGenerationFault)
    {
        try
        {
            await startup.GenerationCompletion.ConfigureAwait(false);
            _completion.TrySetResult();
        }
        catch (OperationCanceledException) when (IsStopping)
        {
            _completion.TrySetResult();
        }
        catch (Exception) when (IsStopping)
        {
            _completion.TrySetResult();
        }
        catch (Exception exception)
        {
            Volatile.Write(ref _generationFaultObserved, 1);
            try
            {
                await reportGenerationFault(
                    "agent.runtime.generation-fault",
                    "The Agent Runtime generation failed startup or lost durable ownership and stopped.",
                    CancellationToken.None).ConfigureAwait(false);
            }
            catch
            {
                // The Host may already be withdrawing this exact generation.
            }

            DeactivateProviders();
            _ = DisposeCatalog();
            _ = await StopStartupBoundedAsync(startup).ConfigureAwait(false);
            _completion.TrySetException(exception);
        }
    }

    private bool IsStopping => Volatile.Read(ref _stopping) != 0;

    private void DeactivateProviders()
    {
        foreach (var provider in _providers) provider.Deactivate();
    }

    private Exception? DisposeCatalog()
    {
        if (Interlocked.Exchange(ref _catalogDisposed, 1) != 0) return null;
        try
        {
            _catalog().Dispose();
            return null;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private async Task<Exception?> StopStartupBoundedAsync(AgentRuntimeStartupService startup)
    {
        Task cleanup;
        try
        {
            cleanup = startup.StopAsync(CancellationToken.None);
        }
        catch (Exception exception)
        {
            return exception;
        }

        try
        {
            await cleanup.WaitAsync(_cleanupTimeout).ConfigureAwait(false);
            return null;
        }
        catch (TimeoutException exception)
        {
            _ = ObserveCleanupAsync(cleanup);
            return exception;
        }
        catch (Exception exception)
        {
            return exception;
        }
    }

    private static async Task ObserveCleanupAsync(Task cleanup)
    {
        try
        {
            await cleanup.ConfigureAwait(false);
        }
        catch
        {
            // The original generation fault remains authoritative.
        }
    }
}
