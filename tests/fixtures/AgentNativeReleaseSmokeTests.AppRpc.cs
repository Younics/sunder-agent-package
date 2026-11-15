using System.Buffers.Binary;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Core.TestSupport;
using Sunder.Runtime.Host.Services;
using Sunder.Runtime.LocalState;
using Xunit;

namespace Sunder.Runtime.Host.Tests;

public sealed partial class AgentNativeReleaseSmokeTests
{
    // Commands run in the actual App image, not in a Worker, a fabricated peer or
    // an in-process broker caller. The hook is test-only and never enters a package.
    private sealed class NativeAppRpcProxy(CapturedProcess process, string responsePath, string diagnosticsPath) : IAsyncDisposable
    {
        private readonly SemaphoreSlim _commands = new(1, 1);
        private int _disposed;
        private int ProcessId => process.Id;

        internal static async Task<NativeAppRpcProxy> CreateAsync(IServiceProvider services, string packageId, string rid,
            RuntimeConnectionInfo connection, string root)
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "sunder-agent-native-app-target-path.txt");
            Assert.True(File.Exists(marker), "The native test build must record its exact Sunder.App output.");
            var assembly = (await File.ReadAllTextAsync(marker)).Trim();
            var executable = Path.Combine(Path.GetDirectoryName(assembly)!, OperatingSystem.IsWindows() ? "Sunder.App.exe" : "Sunder.App");
            var hook = Path.Combine(AppContext.BaseDirectory, "agent-native-app-hook", "Sunder.App.Tests.dll");
            Assert.True(File.Exists(assembly) && File.Exists(executable) && File.Exists(hook), "Build the selected App and its native RPC hook first.");
            var snapshot = Assert.Single(services.GetRequiredService<RuntimePackageUiService>().GetActiveSnapshots(rid), item => item.PackageId == packageId);
            var source = services.GetRequiredService<PackageSessionState>().GetActivePackageSources().Single(item => item.PackageId == packageId);
            Directory.CreateDirectory(root);
            var connectionPath = Path.Combine(root, "connection.json");
            RuntimeConnectionInfoStore.Save(connection, connectionPath);
            var configPath = Path.Combine(root, "config.json");
            var responsePath = Path.Combine(root, "response.json");
            await File.WriteAllBytesAsync(configPath, JsonSerializer.SerializeToUtf8Bytes(new
            {
                connectionPath, responsePath, snapshot,
                manifestSha256 = RuntimeRpcPackageMetadata.GetManifestSha256(source.ContentIndex!),
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false, RedirectStandardInput = true, WorkingDirectory = Path.GetDirectoryName(executable)!,
            };
            start.Environment["DOTNET_STARTUP_HOOKS"] = hook;
            start.Environment["SUNDER_TEST_NATIVE_APP_RPC_CONFIG"] = configPath;
            start.Environment["SUNDER_APP_STATE_ROOT"] = Path.Combine(root, "app-state");
            start.Environment["SUNDER_HOME"] = Path.Combine(root, "home");
            start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
            foreach (var name in new[] { "GH_TOKEN", "GITHUB_TOKEN", "SUNDER_REGISTRY_CLI_TOKEN", "SUNDER_REGISTRY_PUBLISH_TOKEN", "SUNDER_AGENT_NATIVE_REGISTRY_TOKEN" })
                start.Environment.Remove(name);
            var diagnostics = Path.Combine(RequiredMakerPath("SUNDER_AGENT_NATIVE_METRICS_DIR", directory: true), "apps", packageId, Guid.NewGuid().ToString("N"));
            var proxy = new NativeAppRpcProxy(CapturedProcess.Start(start, maximumOutputCharacters: 1024 * 1024, diagnosticsDirectory: diagnostics), responsePath, diagnostics);
            try
            {
                var ready = await proxy.ReadResponseAsync("ready", CancellationToken.None);
                Assert.Equal(proxy.ProcessId, ready.GetProperty("processId").GetInt32());
                Assert.Equal(packageId, ready.GetProperty("packageId").GetString());
                return proxy;
            }
            catch (Exception failure)
            {
                try { await proxy.DisposeAsync(); }
                catch (Exception cleanup) { throw new AggregateException("Native App startup and cleanup both failed.", failure, cleanup); }
                throw;
            }
        }

        internal async Task<JsonElement> InvokeAsync(string contract, string service, string method, JsonElement request,
            bool content, CancellationToken token = default, long? afterRevision = null)
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            await _commands.WaitAsync(token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                var id = Guid.NewGuid().ToString("N");
                var bytes = JsonSerializer.SerializeToUtf8Bytes(new { id, contract, service, method, request, content, afterRevision });
                Assert.InRange(bytes.Length, 1, 256 * 1024);
                var header = new byte[4];
                BinaryPrimitives.WriteInt32LittleEndian(header, bytes.Length);
                await process.Process.StandardInput.BaseStream.WriteAsync(header, deadline.Token);
                await process.Process.StandardInput.BaseStream.WriteAsync(bytes, deadline.Token);
                await process.Process.StandardInput.BaseStream.FlushAsync(deadline.Token);
                return await ReadResponseAsync(id, deadline.Token);
            }
            finally { _commands.Release(); }
        }

        private async Task<JsonElement> ReadResponseAsync(string id, CancellationToken token)
        {
            await process.WaitForOutputAsync(output => output.Contains("SUNDER_NATIVE_APP_RPC:" + id + "\n", StringComparison.Ordinal), TimeSpan.FromSeconds(40), token);
            Assert.InRange(new FileInfo(responsePath).Length, 1, 16 * 1024 * 1024);
            using var result = JsonDocument.Parse(await File.ReadAllBytesAsync(responsePath, token));
            Assert.Equal(id, result.RootElement.GetProperty("id").GetString());
            Assert.True(result.RootElement.GetProperty("success").GetBoolean(), result.RootElement.ToString() + Environment.NewLine + process.Diagnostics);
            return result.RootElement.GetProperty("response").Clone();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try
            {
                process.Process.StandardInput.Close();
                await process.EnsureSuccessAsync(TimeSpan.FromSeconds(15));
            }
            finally
            {
                try { await File.WriteAllTextAsync(Path.Combine(diagnosticsPath, "process.txt"), process.Diagnostics); }
                finally { await process.DisposeAsync(); _commands.Dispose(); }
            }
        }
    }
}
