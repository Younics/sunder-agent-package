using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private sealed class FilesTargetProbe(ISunderRpcServiceHandler inner) : ISunderRpcServiceHandler, ISunderRpcDeclaredErrorMetadata
    {
        internal bool EmptyFacets;
        internal bool DisableShell;
        internal Action<JsonElement>? BeforeWrite;
        internal FilesCallGate? BlockRead;
        internal ConcurrentQueue<(string Method, JsonElement Request, JsonElement Response)> Calls { get; } = new();
        public IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> DeclaredErrors => ((ISunderRpcDeclaredErrorMetadata)inner).DeclaredErrors;
        public async ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
        {
            if (methodId == "read-file" && BlockRead is { } gate)
            {
                gate.Started.TrySetResult();
                try { await gate.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { gate.Cancelled.TrySetResult(); throw; }
            }
            if (methodId == "write-file") BeforeWrite?.Invoke(request);
            var value = await inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);
            if (methodId == "describe" && (EmptyFacets || DisableShell))
            {
                var rewritten = JsonNode.Parse(value.GetRawText())!;
                if (EmptyFacets) rewritten["facets"] = new JsonArray();
                if (DisableShell) rewritten["supportsShell"] = false;
                value = JsonSerializer.SerializeToElement(rewritten);
            }
            Calls.Enqueue((methodId, request.Clone(), value.Clone()));
            return value;
        }
        public IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId, string methodId, JsonElement request, CancellationToken token = default)
            => inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token);
    }

    private sealed class FilesCallGate
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Cancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class FilesRpcObserver(ISunderRpcClient inner, string methodToInvalidate = "read-file") : ISunderRpcClient
    {
        internal ConcurrentBag<Scope> Scopes { get; } = [];
        internal Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>? Transform;
        internal SunderRpcErrorKind? LookupFailure;
        internal bool InvalidateAfterInvoke;
        private string MethodToInvalidate => methodToInvalidate;
        internal int UnscopedLookups;
        internal int ActiveWatches;
        public async ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
        {
            var scope = new Scope(this, await inner.CreateCallScopeAsync(options, cancellationToken)); Scopes.Add(scope); return scope;
        }
        public ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref UnscopedLookups); return inner.GetProviderAsync(endpoint, cancellationToken); }
        public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
            => inner.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
        public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => inner.DiscoverAsync(contractId, cancellationToken);
        public async IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref ActiveWatches);
            try { await foreach (var item in inner.WatchAsync(afterRevision, afterSequence, cancellationToken)) yield return item; }
            finally { Interlocked.Decrement(ref ActiveWatches); }
        }
        public ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Files execution calls must use an owned scope.");
        public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Files execution has no contract streams.");

        internal sealed class Scope(FilesRpcObserver owner, ISunderRpcCallScope innerScope) : ISunderRpcCallScope
        {
            internal bool Disposed;
            public DateTimeOffset DeadlineUtc => innerScope.DeadlineUtc;
            public async ValueTask DisposeAsync() { await innerScope.DisposeAsync(); Disposed = true; }
            public ValueTask<ISunderRpcCallScope> CreateCallScopeAsync(SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
                => owner.CreateCallScopeAsync(options, cancellationToken);
            public async ValueTask<SunderRpcProviderSnapshot?> GetProviderAsync(SunderRpcEndpointReference endpoint, CancellationToken cancellationToken = default)
            {
                if (owner.LookupFailure is { } kind) throw new SunderRpcException(new(kind, "files.test.lookup", "Controlled lookup failure."));
                var value = await innerScope.GetProviderAsync(endpoint, cancellationToken);
                return value is not null && owner.Transform is { } transform ? transform(value) : value;
            }
            public ValueTask<bool> TryReportInvariantViolationAsync(SunderRpcEndpointReference endpoint, Exception exception, CancellationToken cancellationToken = default)
                => innerScope.TryReportInvariantViolationAsync(endpoint, exception, cancellationToken);
            public ValueTask<SunderRpcCatalogSnapshot> DiscoverAsync(string contractId, CancellationToken cancellationToken = default) => innerScope.DiscoverAsync(contractId, cancellationToken);
            public IAsyncEnumerable<SunderRpcCatalogEvent> WatchAsync(long afterRevision, long afterSequence, CancellationToken cancellationToken = default) => innerScope.WatchAsync(afterRevision, afterSequence, cancellationToken);
            public async ValueTask<JsonElement> InvokeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
            {
                var value = await innerScope.InvokeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
                if (methodId == owner.MethodToInvalidate && owner.InvalidateAfterInvoke) owner.Transform = provider => provider with { SessionGeneration = provider.SessionGeneration + 1 };
                return value;
            }
            public IAsyncEnumerable<JsonElement> SubscribeAsync(SunderRpcEndpointReference endpoint, string serviceId, string methodId, JsonElement request, SunderRpcCallOptions? options = null, CancellationToken cancellationToken = default)
                => innerScope.SubscribeAsync(endpoint, serviceId, methodId, request, options, cancellationToken);
            public ValueTask<SunderRpcContentReference> RegisterContentAsync(SunderRpcEndpointReference endpoint, Stream source, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                => innerScope.RegisterContentAsync(endpoint, source, options, cancellationToken);
            public ValueTask<SunderRpcContentReference> RegisterContentFileAsync(SunderRpcEndpointReference endpoint, string path, SunderRpcContentRegistrationOptions options, CancellationToken cancellationToken = default)
                => innerScope.RegisterContentFileAsync(endpoint, path, options, cancellationToken);
            public ValueTask<Stream> OpenContentAsync(SunderRpcContentReference reference, CancellationToken cancellationToken = default) => innerScope.OpenContentAsync(reference, cancellationToken);
        }
    }
}
