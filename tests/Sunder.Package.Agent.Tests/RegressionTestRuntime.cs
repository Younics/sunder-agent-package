using System.Reflection;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tests;

// Uses the canonical generated graph. No legacy service-registration list lives
// in tests; lifecycle scenarios decide when to prepare/commit/activate it.
internal sealed class RegressionTestRuntime : IServiceProvider, IDisposable, IAsyncDisposable
{
    private readonly IServiceProvider _services;
    private readonly IAsyncDisposable _lifetime;
    private IDisposable? _ownedCatalog;

    private RegressionTestRuntime(IPackageContext context, ISunderRpcClient rpc)
    {
        var worker = typeof(AgentRunControlService).Assembly;
        var entry = worker.GetType("Sunder.Generated.Package.WorkerEntryPoint", throwOnError: true)!;
        var create = entry.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(IPackageContext), typeof(ISunderRpcClient)])
            ?? throw new InvalidOperationException("The canonical Worker entry point must expose generated composition.");
        Options = (SunderWorkerOptions)create.Invoke(null, [context, rpc])!;
        var composition = Options.Composition
            ?? throw new InvalidOperationException("The current Agent Worker must use staged generated composition.");
        _services = composition.Services;
        _lifetime = composition;
    }

    internal SunderWorkerOptions Options { get; }

    internal static RegressionTestRuntime Create(IPackageContext context)
    {
        var catalog = new RegressionTestExtensionCatalog();
        try { return new(context, catalog.RpcForCaller("sunder.package.agent")) { _ownedCatalog = catalog }; }
        catch { catalog.Dispose(); throw; }
    }

    internal static RegressionTestRuntime Create(IPackageContext context, ISunderRpcClient rpc) => new(context, rpc);

    internal static RegressionTestRuntime Create(IPackageContext context, RegressionTestExtensionCatalog catalog)
        => new(context, catalog.RpcForCaller("sunder.package.agent"));

    public object? GetService(Type serviceType) => _services.GetService(serviceType);
    public async ValueTask DisposeAsync()
    {
        try { await _lifetime.DisposeAsync().ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _ownedCatalog, null)?.Dispose(); }
    }
    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
}
