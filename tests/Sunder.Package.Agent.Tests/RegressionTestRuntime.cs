using System.Reflection;
using Sunder.Package.Agent.Protocol;
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
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(typeof(Rpc.Public.RunControlService).Assembly, context, rpc);
        Options = generated.Options;
        _services = generated.Services;
        _lifetime = generated.Lifetime;
    }

    internal SunderWorkerOptions Options { get; }

    internal static RegressionTestRuntime Create(IPackageContext context)
    {
        var catalog = new RegressionTestExtensionCatalog();
        try { return new(context, catalog.RpcForCaller("sunder.package.agent")) { _ownedCatalog = catalog }; }
        catch { catalog.Dispose(); throw; }
    }

    internal static RegressionTestRuntime Create(IPackageContext context, ISunderRpcClient rpc) => new(context, rpc);

    internal static RegressionTestRuntime Create(IPackageContext context, AgentRpcCatalog catalog)
        => new(context, (ISunderRpcClient)typeof(AgentRpcCatalog).GetField("_client", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(catalog)!);

    public object? GetService(Type serviceType) => _services.GetService(serviceType);
    public async ValueTask DisposeAsync()
    {
        try { await _lifetime.DisposeAsync().ConfigureAwait(false); }
        finally { Interlocked.Exchange(ref _ownedCatalog, null)?.Dispose(); }
    }
    public void Dispose() => Task.Run(async () => await DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
}
