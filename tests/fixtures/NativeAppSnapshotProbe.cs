using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;

// Agent's App contribution must materialize from a Runtime-produced snapshot.
// The signed App process retains control of admission and retirement.
internal static class NativeAppSnapshotProbe
{
    internal static async Task RunAsync(string configPath)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(configPath, token));
        var config = document.RootElement;
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        string Read(string name) => config.GetProperty(name).GetString()!;
        var connection = RuntimeConnectionInfoStore.Load(Read("connectionPath")) ?? throw new InvalidDataException("Missing private snapshot connection.");
        using var gateway = new RuntimeClientTransport(() => connection, RuntimeIpcHttpMessageHandlerFactory.Create(connection.IpcEndpoint!));
        using var direct = new AppRuntimeProcessTransport(gateway);
        await using var policy = new AppProcessPolicySupervisor(direct, () => Environment.Exit(79));
        var package = config.GetProperty("package").Deserialize<ActivePackageDescriptor>(json)!;
        var snapshot = config.GetProperty("snapshot").Deserialize<PackageUiSnapshotDescriptor>(json)!;
        if (snapshot.SourceKind != PackageSourceKind.Dev || package.PackageId != "younics.ai.agent")
            throw new InvalidDataException("Snapshot qualification requires the Agent development owner.");
        await using (var host = new PackageViewHostService(new AppPackageViewRegistry(), [], [], [], Read("sessionRoot"),
            getRuntimeConnectionInfo: direct.Transport.GetConnectionInfo,
            downloadPackageUiSnapshotAsync: async (requested, destination, cancellation) =>
            {
                if (requested != snapshot) throw new InvalidDataException("Unexpected App artifact request.");
                await using var source = File.OpenRead(Read("snapshotPath"));
                await source.CopyToAsync(destination, cancellation);
            }, uiDispatcher: new ImmediateDispatcher(), packagePolicy: policy, runtimeStateRootPath: Read("runtimeRoot")))
        {
            await host.ApplyPackageDeltaAsync([package], [snapshot], cancellationToken: token);
            var active = host.FilterEnabledPackages([package]).Single();
            if (host.LoadedPackageCount != 1 || !active.Views.Any(view => view.ViewId == "younics.ai.agent.chat"))
                throw new InvalidDataException("The exported owner artifact did not activate Agent Chat.");
            Console.WriteLine("agent-family-app: materialized-and-activated");
            if (await Console.OpenStandardInput().ReadAsync(new byte[1], token) != 0)
                throw new InvalidDataException("Unexpected App retirement command.");
        }
        Console.WriteLine("agent-family-app: retired");
    }

    private sealed class ImmediateDispatcher : IUiDispatcher
    {
        public bool CheckAccess() => true;
        public Task InvokeAsync(Action action) { action(); return Task.CompletedTask; }
        public Task InvokeAsync(Func<Task> action) => action();
        public Task<T> InvokeAsync<T>(Func<T> action) => Task.FromResult(action());
        public Task<T> InvokeAsync<T>(Func<Task<T>> action) => action();
    }
}
