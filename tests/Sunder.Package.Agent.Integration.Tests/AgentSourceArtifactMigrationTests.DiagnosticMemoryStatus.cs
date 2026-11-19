using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    // Diagnostic ref only. Reuse the same fresh owner and Memory artifact helpers,
    // without selecting or claiming the other thirteen family artifacts.
    [AgentPublicSourceMigrationFact]
    public async Task Diagnostic_MemoryArtifact_StatusDelivery()
    {
        var repository = BuilderSourceMigrationTests.FindRepository();
        var core = Path.GetFullPath(Path.Combine(repository, "..", "sunder-core"));
        var driver = Environment.GetEnvironmentVariable("SUNDER_AGENT_CORE_TEST_DRIVER");
        Assert.True(File.Exists(driver), "Use the verified Core test driver.");
        var root = Directory.CreateTempSubdirectory("sunder-agent-memory-status-diagnostic-").FullName;
        var completed = false;
        try
        {
            var cache = Path.Combine(root, "fixture-cache");
            await PrepareOwnerArtifact(root, driver!, cache);
            await VerifyMemoryWorkerArtifact(root, core, driver!, cache);
            completed = true;
        }
        finally
        {
            try { RetainArtifactAttempt(root, completed); }
            finally { Directory.Delete(root, recursive: true); }
        }
    }

    private sealed class MemoryStatusTrace : IDisposable
    {
        private readonly ConcurrentQueue<string> _trace = new();
        private readonly long _started = Stopwatch.GetTimestamp();
        private readonly string? _path = Environment.GetEnvironmentVariable("SUNDER_AGENT_MEMORY_STATUS_TRACE");
        public bool Enabled => _path is not null;

        public void Note(string mark)
        {
            if (Enabled)
                _trace.Enqueue(FormattableString.Invariant($"{Stopwatch.GetElapsedTime(_started).TotalMilliseconds:F3}\t{mark}"));
        }

        public void Dispose()
        {
            if (_path is not null)
                File.WriteAllLines(_path, _trace);
        }
    }

    private sealed class MemoryStatusTraceHandler(ISunderRpcServiceHandler inner, MemoryStatusTrace trace) : ISunderRpcServiceHandler
    {
        public ValueTask<JsonElement> InvokeUnaryAsync(SunderRpcInvocationContext context, string serviceId,
            string methodId, JsonElement request, CancellationToken token = default)
            => inner.InvokeUnaryAsync(context, serviceId, methodId, request, token);

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(SunderRpcInvocationContext context, string serviceId,
            string methodId, JsonElement request, [EnumeratorCancellation] CancellationToken token = default)
        {
            trace.Note("rpc.open:" + methodId);
            try
            {
                await foreach (var item in inner.InvokeServerStreamAsync(context, serviceId, methodId, request, token))
                {
                    trace.Note("rpc.kind:" + (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("kind", out var kind)
                        ? kind.GetRawText() : item.ValueKind.ToString()));
                    yield return item;
                }
            }
            finally
            {
                trace.Note("rpc.close:" + methodId);
            }
        }
    }
}
