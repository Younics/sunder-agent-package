extern alias MemoryWorker;

using System.Reflection;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using Microsoft.Extensions.DependencyInjection;

namespace Sunder.Package.Agent.Tests;

// Seed and inspect the activated Worker's store, not a second store backed by
// the test host's SQLite loader. Two native SQLite copies in the same process
// must not open the same database. The worker lifetime owns this store.
internal sealed class MemoryWorkerStoreFixture(Assembly worker, IServiceProvider services)
{
    private readonly object _store = services.GetRequiredService(
        worker.GetType(typeof(MemoryLocalStore).FullName!, throwOnError: true)!);

    internal StoredMemoryRecord UpsertMemory(MemoryUpsertRequest request)
        => AgentRoleBoundary.As<StoredMemoryRecord>(
            AgentRoleBoundary.Call(_store, nameof(MemoryLocalStore.UpsertMemory), request, null)!);

    internal StoredMemoryRecord? GetMemory(Guid memoryId)
        => AgentRoleBoundary.Call(_store, nameof(MemoryLocalStore.GetMemory), memoryId) is { } memory
            ? AgentRoleBoundary.As<StoredMemoryRecord>(memory)
            : null;
}
