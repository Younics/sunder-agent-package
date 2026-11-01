using System.Collections.Concurrent;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.TestSupport;

/// <summary>Records admitted App-state updates for Maker and generated composition fixtures.</summary>
public sealed class BuilderUiStateUpdates : IPackageAppStateUpdates
{
    public bool Accepting = true;
    public CancellationToken SettlementRequested;
    public ConcurrentQueue<Task> Accepted { get; } = new();

    public Task RunAsync(string key, Func<PackageAppStateUpdateContext, Task> update, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Accepting) throw new InvalidOperationException("Test App admission is closed.");
        var accepted = Task.Run(() => update(new(SettlementRequested, cancellationToken)));
        Accepted.Enqueue(accepted);
        return accepted;
    }
}
