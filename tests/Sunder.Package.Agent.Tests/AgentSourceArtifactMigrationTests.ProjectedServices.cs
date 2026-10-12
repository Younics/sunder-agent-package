using System.Collections;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    // Reflection crosses only the collectible CLR boundary. The selected instance,
    // reference, generated client, marshalling and retirement remain SDK-owned.
    private static async Task<ProjectedArtifactProvider[]> ProjectedProviders(
        GeneratedServiceTestScope receiver, Assembly module, string service)
    {
        var contract = module.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services." + service, true)!;
        var catalog = receiver.Providers(contract);
        var result = (await AgentRoleBoundary.Await(catalog.GetType().GetMethod("ListAsync")!
            .Invoke(catalog, [CancellationToken.None])!))!;
        return ((IEnumerable)result.GetType().GetProperty("Providers")!.GetValue(result)!)
            .Cast<object>().Select(provider => new ProjectedArtifactProvider(provider)).ToArray();
    }

    private sealed class ProjectedArtifactProvider(object provider)
    {
        internal object Reference => provider.GetType().GetProperty("Reference")!.GetValue(provider)!;
        internal RoleLocalValue Service => new(provider.GetType().GetProperty("Service")!.GetValue(provider)!);
        internal CancellationToken RetirementToken => (CancellationToken)Reference.GetType().GetProperty("RetirementToken")!.GetValue(Reference)!;
        internal SunderRpcProviderSnapshot Snapshot => (SunderRpcProviderSnapshot)Reference.GetType()
            .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Reference)!;
        internal async Task EnsureCurrentAsync()
        {
            try { await (ValueTask)Reference.GetType().GetMethod("EnsureCurrentAsync")!.Invoke(Reference, [CancellationToken.None])!; }
            catch (TargetInvocationException error) when (error.InnerException is not null)
            { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; }
        }
    }

    private static async IAsyncEnumerable<JsonElement> ProjectedCatalogEvents(object service,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        var stream = (IAsyncEnumerable<object>)service.GetType().GetMethod("WatchAsync")!.Invoke(service, [token])!;
        await foreach (var value in stream.WithCancellation(token))
            yield return JsonSerializer.SerializeToElement(value, value.GetType(), MemoryProviderJson);
    }

    private sealed class ProjectedArtifactWatch(object service) : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private IAsyncEnumerator<JsonElement>? _events;
        private Task<bool>? _pending;
        private bool _disposed;
        internal JsonElement Current => _events!.Current;
        internal Task<bool> Next()
        {
            _events ??= ProjectedCatalogEvents(service, _cancellation.Token).GetAsyncEnumerator();
            _pending = _events.MoveNextAsync().AsTask();
            return _pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        public async ValueTask DisposeAsync()
        {
            if (_disposed) return;
            _disposed = true;
            await _cancellation.CancelAsync();
            if (_pending is not null)
            {
                try { await _pending.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) { }
                catch (SunderRpcException) { }
            }
            if (_events is not null) await _events.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            _cancellation.Dispose();
        }
    }
}
