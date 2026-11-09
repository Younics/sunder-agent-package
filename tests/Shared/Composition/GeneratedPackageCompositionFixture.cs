using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Reflection negotiates the actual generated graph in its owning role assembly.
// Ordinary component suites must not borrow this from an executable artifact test.
internal static partial class GeneratedPackageCompositionFixture
{
    internal static (SunderWorkerOptions Options, IAsyncDisposable Lifetime, IServiceProvider Services) CreateGeneratedWorker(
        Assembly worker, IPackageContext context, ISunderRpcClient rpc)
    {
        Assert.Equal("Sunder.Generated.Package.WorkerEntryPoint", worker.EntryPoint!.DeclaringType!.FullName);
        var entry = worker.GetType("Sunder.Generated.Package.WorkerEntryPoint", true)!;
        var options = (SunderWorkerOptions)entry.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic,
            [typeof(IPackageContext), typeof(ISunderRpcClient)])!.Invoke(null, [context, rpc])!;
        return GetGeneratedComposition(options);
    }

    internal static (SunderWorkerOptions Options, IAsyncDisposable Lifetime, IServiceProvider Services) GetGeneratedComposition(SunderWorkerOptions options)
    {
        if (options.Composition is { } staged) return (options, staged, staged.Services);
        var shutdown = options.OnShutdown!;
        var services = (IServiceProvider)shutdown.Target!.GetType().GetProperty("Services", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(shutdown.Target)!;
        return (options, new GeneratedShutdownLifetime(shutdown), services);
    }

    internal static async Task ActivateGeneratedComposition(SunderWorkerOptions options)
    {
        var context = (SunderWorkerContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SunderWorkerContext));
        if (options.Composition is { } composition)
        {
            await Stage("PrepareCandidateAsync", CancellationToken.None);
            var generation = Activator.CreateInstance(typeof(SunderWorkerGenerationContext), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Guid.NewGuid(), 1L], culture: null)!;
            await Stage("CommitGenerationAsync", generation, CancellationToken.None);
            await Stage("ActivateAsync", context, CancellationToken.None);

            Task Stage(string name, params object[] arguments)
                => (Task)typeof(SunderWorkerComposition).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(composition, arguments)!;
        }
        if (options.OnActivated is { } activate) await activate(context, CancellationToken.None);
    }

    private sealed class GeneratedShutdownLifetime(Func<SunderWorkerShutdownContext, CancellationToken, ValueTask> shutdown) : IAsyncDisposable
    {
        private readonly SunderWorkerShutdownContext _reason = (SunderWorkerShutdownContext)Activator.CreateInstance(typeof(SunderWorkerShutdownContext),
            BindingFlags.Instance | BindingFlags.NonPublic, binder: null, args: ["artifact-shutdown"], culture: null)!;
        public ValueTask DisposeAsync() => shutdown(_reason, CancellationToken.None);
    }
}
