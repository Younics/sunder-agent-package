using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.TestSupport;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias MemoryWorker;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task ExerciseMemoryWorkerProcess(string root, string runtimeRoot, JsonElement manifest)
    {
        var workspace = Directory.CreateDirectory(Path.Combine(root, "MemoryProcessWorkspace")).FullName;
        var database = Path.Combine(workspace, "memory/agent-memory.db");
        await using (var candidate = new PackageWorkerPeer(root, runtimeRoot, workspace, manifest))
        {
            await candidate.Prepare();
            Assert.Equal(JsonValueKind.Object, candidate.Contributions.GetProperty("settingsSchema").ValueKind);
            Assert.False(File.Exists(database));
            await candidate.Shutdown();
            Assert.False(File.Exists(database));
        }
        await using (var active = new PackageWorkerPeer(root, runtimeRoot, workspace, manifest))
        {
            await active.Prepare();
            await active.Activate();
            Assert.True(File.Exists(database));
            var capabilities = await active.Invoke("semantic.memory.profile.capabilities", "profile-capability-consumer", "describe", new { });
            Assert.Equal("memory.semantic", capabilities.GetProperty("consumerId").GetString());
            Assert.Equal(AgentModelCapabilityKinds.Embedding, Assert.Single(capabilities.GetProperty("capabilities").EnumerateArray()).GetProperty("capabilityKind").GetString());
            await active.Shutdown();
        }
        var context = new TestPackageContext("sunder.package.agent.memory.semantic", workspace);
        var removedSession = Guid.NewGuid();
        var retainedSession = Guid.NewGuid();
        Guid removed, retained;
        await using (var seed = new MemoryLocalStore(context))
        {
            removed = seed.UpsertMemory(new(removedSession, "fact", "remove on worker", "remove on worker", "evidence", null, false, 0.8f, 0.9f)).MemoryId;
            retained = seed.UpsertMemory(new(retainedSession, "fact", "retain on worker", "retain on worker", "evidence", null, false, 0.8f, 0.9f)).MemoryId;
        }
        await using (var restarted = new PackageWorkerPeer(root, runtimeRoot, workspace, manifest))
        {
            await restarted.Prepare();
            await restarted.Activate();
            await restarted.Invoke("semantic.memory.session.cleaner", "session-cleaner", "delete-session", new { sessionId = removedSession });
            await restarted.Invoke("semantic.memory.session.cleaner", "session-cleaner", "delete-session", new { sessionId = removedSession });
            await restarted.Shutdown();
        }
        await using var reopened = new MemoryLocalStore(context);
        Assert.Null(reopened.GetMemory(removed));
        Assert.Empty(reopened.ListEvidence(removed));
        Assert.NotNull(reopened.GetMemory(retained));
    }

    private sealed class PackageWorkerPeer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly Task<string> _diagnostics;
        private readonly string _activation = Guid.NewGuid().ToString("N");
        private readonly string _workspace;
        private readonly string _packageId;
        private readonly string _packageVersion;
        private readonly JsonElement[] _providers;
        private readonly HashSet<string> _streams = new(StringComparer.Ordinal);
        private readonly HashSet<string> _scopes = new(StringComparer.Ordinal);
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(90));
        private int _call;
        private bool _active;
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        internal JsonElement Contributions { get; private set; }
        internal int StateReads { get; private set; }

        internal PackageWorkerPeer(string root, string runtimeRoot, string workspace, JsonElement manifest)
        {
            _workspace = workspace;
            _packageId = manifest.GetProperty("id").GetString()!;
            _packageVersion = manifest.GetProperty("version").GetString()!;
            _providers = manifest.GetProperty("provides").EnumerateArray().Select(value => value.Clone()).ToArray();
            var temporary = Directory.CreateDirectory(Path.Combine(root, "process-" + _activation)).FullName;
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.Combine(runtimeRoot, "lib"), RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false,
            };
            var target = Assert.Single(manifest.GetProperty("targets").EnumerateArray(), value => value.GetProperty("role").GetString() == "runtime");
            start.ArgumentList.Add(Path.Combine(runtimeRoot, target.GetProperty("entryPoint").GetString()!));
            start.Environment["SUNDER_WORKER_PROTOCOL"] = "sunder.worker.v1";
            start.Environment["SUNDER_PACKAGE_ID"] = _packageId;
            start.Environment["SUNDER_PACKAGE_VERSION"] = _packageVersion;
            start.Environment["SUNDER_ACTIVATION_ID"] = _activation;
            start.Environment["SUNDER_SESSION_ID"] = "memory-source-test";
            start.Environment["SUNDER_PACKAGE_CONTENT_ROOT"] = runtimeRoot;
            start.Environment["SUNDER_WORKER_TEMP_ROOT"] = temporary;
            start.Environment["HOME"] = temporary;
            start.Environment["USERPROFILE"] = temporary;
            start.Environment["APPDATA"] = temporary;
            _process = Process.Start(start)!;
            _diagnostics = ReadDiagnostics(_process.StandardError, _deadline.Token);
        }

        internal async Task Prepare()
        {
            var profile = new { version = 1, rpcValueBytes = 8 * 1024 * 1024, valueDepth = 64, valueNodes = 524288, objectMembers = 4096, inlineDecodedBytes = 512 * 1024, contentReferenceBytes = 64 * 1024 * 1024 };
            var identities = _providers.Select(provider => new
            {
                providerId = provider.GetProperty("providerId").GetString(), contractId = provider.GetProperty("contractId").GetString(),
                contractVersion = provider.GetProperty("contractVersion").GetString(), descriptorSha256 = provider.GetProperty("descriptorSha256").GetString(),
                abiSha256 = provider.GetProperty("abiSha256").GetString(),
            }).ToArray();
            await Send(new { type = "host.hello", protocol = "sunder.worker.v1", protocolVersion = 1, challenge = "memory-source",
                packageId = _packageId, packageVersion = _packageVersion, activationId = _activation, sessionId = "memory-source-test", providers = identities,
                resourceProfile = profile, resourceProfileHash = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(profile, Json))) });
            var ready = await Until("worker.ready");
            Contributions = ready.GetProperty("contributions").Clone();
            Assert.Equal(_providers.Length, Contributions.GetProperty("rpcProviders").GetArrayLength());
            await Send(new { type = "host.start-candidate" });
            await Until("worker.candidate-started");
            await Send(new { type = "host.commit-generation", activationId = _activation, sessionGeneration = 1 });
            await Until("worker.generation-committed");
            await Send(new { type = "host.prepare-storage-migrations", activationId = _activation, sessionGeneration = 1 });
            await Until("worker.storage-migrations-prepared");
        }

        internal async Task Activate()
        {
            _active = true;
            await Send(new { type = "host.activate", sessionGeneration = 1, workspaceRootPath = _workspace });
            await Until("worker.activated");
        }

        internal async Task<JsonElement> Invoke(string providerId, string serviceId, string methodId, object request)
        {
            var declaration = Assert.Single(_providers, value => value.GetProperty("providerId").GetString() == providerId);
            var id = "invoke-" + ++_call;
            await Send(new { type = "host.invoke", id, kind = "unary", providerId, serviceId, methodId, request,
                context = new { callerPackageId = "sunder.package.agent", callerPackageVersion = "2.0.0", deadlineUtc = DateTimeOffset.UtcNow.AddMinutes(1).UtcDateTime.ToString("O", CultureInfo.InvariantCulture), callDepth = 1,
                    provider = new { packageId = _packageId, packageVersion = _packageVersion, providerId,
                        contractId = declaration.GetProperty("contractId").GetString(), contractVersion = declaration.GetProperty("contractVersion").GetString(),
                        descriptorSha256 = declaration.GetProperty("descriptorSha256").GetString(), abiSha256 = declaration.GetProperty("abiSha256").GetString(),
                        activationId = _activation, activationEpoch = 1, sessionGeneration = 1, endpointReference = "rpc_memory_source", catalogRevision = 1, state = "active", faultCode = (string?)null } } });
            var result = await Until("worker.result");
            Assert.Equal(id, result.GetProperty("id").GetString());
            return result.GetProperty("value").Clone();
        }

        internal async Task Shutdown()
        {
            await Send(new { type = "host.shutdown", shutdownId = "memory-shutdown", reason = "test-completed" });
            await Until("worker.shutdown-ack");
            await _process.WaitForExitAsync(_deadline.Token);
            Assert.True(_process.ExitCode == 0, await _diagnostics);
        }

        private async Task<JsonElement> Until(string expected)
        {
            while (true)
            {
                using var frame = await ReadExpectedFrame(expected);
                var message = frame.RootElement;
                var type = message.GetProperty("type").GetString();
                if (type == expected) return message.Clone();
                Assert.True(_active, $"Worker '{_packageId}' performed '{type}' before activation: {message}");
                var id = message.TryGetProperty("id", out var correlation) ? correlation.GetString() : null;
                switch (type)
                {
                    case "worker.scope-open":
                        var scopeId = "scope-" + Guid.NewGuid().ToString("N");
                        Assert.True(_scopes.Add(scopeId));
                        var deadline = DateTimeOffset.UtcNow.AddMinutes(1);
                        if (message.TryGetProperty("deadlineUtc", out var requested) && requested.ValueKind == JsonValueKind.String && requested.GetDateTimeOffset() < deadline)
                            deadline = requested.GetDateTimeOffset();
                        await Send(new { type = "host.result", id, value = new { scopeId, deadlineUtc = deadline.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) } });
                        break;
                    case "worker.scope-close":
                        Assert.True(_scopes.Remove(message.GetProperty("scopeId").GetString()!));
                        await Send(new { type = "host.result", id, value = (object?)null });
                        break;
                    case "worker.watch": _streams.Add(id!); break;
                    case "worker.cancel":
                        if (_streams.Remove(id!)) await Send(new { type = "host.complete", id });
                        break;
                    case "worker.discover":
                        await Send(new { type = "host.result", id, value = new { revision = 1, sequence = 0, providers = Array.Empty<object>(), resetRequired = false } });
                        break;
                    case "worker.settings-get":
                        await Send(new { type = "host.result", id, value = new { kind = "inline", value = (string?)null } });
                        break;
                    case "worker.state-get":
                        StateReads++;
                        await Send(new { type = "host.result", id, value = new { kind = "inline", value = (string?)null } });
                        break;
                    case "worker.state-list":
                        StateReads++;
                        await Send(new { type = "host.result", id, value = new { kind = "inline", keys = Array.Empty<string>() } });
                        break;
                    case "worker.logging-write":
                        await Send(new { type = "host.result", id, value = (object?)null });
                        break;
                    default: Assert.Fail($"Unexpected Worker frame for '{_packageId}': {message}"); break;
                }
            }
        }

        private async Task<JsonDocument> ReadExpectedFrame(string expected)
        {
            try { return await ReadFrame(); }
            catch (OperationCanceledException error) when (_deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"Worker '{_packageId}' timed out waiting for '{expected}': " + await _diagnostics, error);
            }
        }

        private async Task Send(object value)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
            try
            {
                await _process.StandardInput.BaseStream.WriteAsync(Encoding.ASCII.GetBytes($"Content-Length: {bytes.Length}\r\n\r\n"), _deadline.Token);
                await _process.StandardInput.BaseStream.WriteAsync(bytes, _deadline.Token);
                await _process.StandardInput.BaseStream.FlushAsync(_deadline.Token);
            }
            catch (IOException error)
            {
                await _process.WaitForExitAsync(_deadline.Token);
                throw new InvalidOperationException($"Worker '{_packageId}' closed its input: " + await _diagnostics, error);
            }
        }

        private async Task<JsonDocument> ReadFrame()
        {
            var header = new List<byte>();
            var single = new byte[1];
            while (header.Count < 1024)
            {
                var read = await _process.StandardOutput.BaseStream.ReadAsync(single, _deadline.Token);
                if (read == 0) throw new InvalidOperationException($"Worker '{_packageId}' exited: " + await _diagnostics);
                header.Add(single[0]);
                if (header.Count >= 4 && header[^4] == '\r' && header[^3] == '\n' && header[^2] == '\r' && header[^1] == '\n') break;
            }
            var text = Encoding.ASCII.GetString(header.ToArray());
            Assert.StartsWith("Content-Length: ", text, StringComparison.Ordinal);
            Assert.EndsWith("\r\n\r\n", text, StringComparison.Ordinal);
            var length = int.Parse(text[16..^4], CultureInfo.InvariantCulture);
            Assert.InRange(length, 1, 8 * 1024 * 1024);
            var bytes = new byte[length];
            await _process.StandardOutput.BaseStream.ReadExactlyAsync(bytes, _deadline.Token);
            return JsonDocument.Parse(bytes);
        }

        private static async Task<string> ReadDiagnostics(StreamReader reader, CancellationToken token)
        {
            var text = new StringBuilder();
            var buffer = new char[4096];
            try
            {
                while (true)
                {
                    var count = await reader.ReadAsync(buffer, token);
                    if (count == 0) return text.ToString();
                    if (text.Length < 65536) text.Append(buffer, 0, Math.Min(count, 65536 - text.Length));
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return text.ToString(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            await _process.WaitForExitAsync();
            try { await _diagnostics; }
            finally { _process.Dispose(); _deadline.Dispose(); }
        }
    }
}
