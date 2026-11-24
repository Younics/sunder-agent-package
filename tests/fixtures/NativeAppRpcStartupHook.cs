using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Sunder.App.Services;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Rpc;

// Friend hook in the real App before Main. The RPC path qualifies the
// supervised backend; the separate syntax probe only exercises App loading.
internal static class StartupHook
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new SunderRpcJsonConverterFactory() },
    };

    public static void Initialize()
    {
        var syntaxProbe = Environment.GetEnvironmentVariable("SUNDER_TEST_MCP_JSON_PROJECTION");
        if (!string.IsNullOrWhiteSpace(syntaxProbe))
        {
            Environment.SetEnvironmentVariable("SUNDER_TEST_MCP_JSON_PROJECTION", null);
            Environment.Exit(McpAppSyntaxProbe.Run(syntaxProbe));
        }
        var path = Environment.GetEnvironmentVariable("SUNDER_TEST_NATIVE_APP_RPC_CONFIG");
        if (string.IsNullOrWhiteSpace(path)) return;
        Environment.SetEnvironmentVariable("SUNDER_TEST_NATIVE_APP_RPC_CONFIG", null);
        var code = RunAsync(path).GetAwaiter().GetResult();
        Environment.Exit(code); // Never fall through to ordinary App startup/live state.
    }

    private static async Task<int> RunAsync(string path)
    {
        using var configDocument = JsonDocument.Parse(await File.ReadAllBytesAsync(path));
        var config = configDocument.RootElement;
        var responsePath = config.GetProperty("responsePath").GetString()!;
        try
        {
            var connection = RuntimeConnectionInfoStore.Load(config.GetProperty("connectionPath").GetString())
                ?? throw new InvalidDataException("Missing native App RPC connection.");
            using var gateway = new RuntimeClientTransport(() => connection, RuntimeIpcHttpMessageHandlerFactory.Create(connection.IpcEndpoint!));
            using var direct = new AppRuntimeProcessTransport(gateway);
            await using var policy = new AppProcessPolicySupervisor(direct, () => Environment.Exit(79));
            var snapshot = config.GetProperty("snapshot").Deserialize<PackageUiSnapshotDescriptor>(Json)!;
            var activation = Guid.NewGuid();
            using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var admission = await policy.AcquireLoadAsync(snapshot, activation, startup.Token);
            try
            {
                var stamp = new ActivePackageWebStamp(snapshot.PackageId, snapshot.PolicySubject!.Version,
                    config.GetProperty("manifestSha256").GetString()!, snapshot.Target, snapshot.SessionGeneration, activation)
                { LoadPermitId = admission.PermitId };
                await using var rpc = await new RuntimeAppWebRpcClientFactory(direct.Transport.GetConnectionInfo).CreateAsync(stamp, startup.Token);
                await WriteResponseAsync(responsePath, "ready", new { processId = Environment.ProcessId, packageId = snapshot.PackageId });
                using var input = Console.OpenStandardInput();
                var header = new byte[4];
                for (var index = 0; index < 10_000; index++)
                {
                    if (await input.ReadAsync(header.AsMemory(0, 1)) == 0) return 0;
                    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    await input.ReadExactlyAsync(header.AsMemory(1), deadline.Token);
                    var length = BinaryPrimitives.ReadInt32LittleEndian(header);
                    if (length is < 1 or > 256 * 1024) throw new InvalidDataException("Native App command exceeds its bound.");
                    var bytes = new byte[length];
                    await input.ReadExactlyAsync(bytes, deadline.Token);
                    using var document = JsonDocument.Parse(bytes);
                    var command = document.RootElement;
                    var id = command.GetProperty("id").GetString()!;
                    if (!Guid.TryParseExact(id, "N", out _)) throw new InvalidDataException("Invalid native App command identity.");
                    try
                    {
                        var response = await InvokeAsync((ISunderRpcClient)rpc, snapshot.PackageId, command, deadline.Token);
                        await WriteResponseAsync(responsePath, id, response);
                    }
                    catch (Exception error) { await WriteFailureAsync(responsePath, id, error); }
                }
                throw new InvalidDataException("Native App command count exceeds its bound.");
            }
            finally
            {
                using var retirement = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                await admission.RetireAsync(retirement.Token);
            }
        }
        catch (Exception error)
        {
            await WriteFailureAsync(responsePath, "ready", error);
            return 78;
        }
    }

    private static async Task<JsonElement> InvokeAsync(ISunderRpcClient rpc, string packageId, JsonElement command, CancellationToken token)
    {
        await using var scope = await rpc.CreateCallScopeAsync(new SunderRpcCallOptions(DateTimeOffset.UtcNow.AddSeconds(30)), token);
        var catalog = await scope.DiscoverAsync(command.GetProperty("contract").GetString()!, token);
        var providers = catalog.Providers.Where(provider => provider.PackageId == packageId).ToArray();
        if (providers.Length != 1) throw new InvalidDataException("Native App RPC must select one exact owner provider.");
        var endpoint = providers[0].Endpoint;
        var service = command.GetProperty("service").GetString()!;
        var method = command.GetProperty("method").GetString()!;
        var request = command.GetProperty("request");
        if (command.GetProperty("afterRevision") is { ValueKind: JsonValueKind.Number } after)
        {
            var revision = after.GetInt64();
            await using var stream = scope.SubscribeAsync(endpoint, service, "changes",
                JsonSerializer.SerializeToElement(new { afterRevision = revision, supportsTurnMutations = true }), cancellationToken: token).GetAsyncEnumerator(token);
            if (!await stream.MoveNextAsync() || stream.Current.GetProperty("revision").GetInt64() < revision)
                throw new InvalidDataException("Native App owner stream did not deliver its initial revision.");
            var mutation = await scope.InvokeAsync(endpoint, service, method, request, cancellationToken: token);
            var changed = mutation.GetProperty("revision").GetInt64();
            if (changed <= revision) throw new InvalidDataException("Native App owner mutation did not advance its revision.");
            while (stream.Current.GetProperty("revision").GetInt64() < changed)
                if (!await stream.MoveNextAsync()) throw new InvalidDataException("Native App owner stream ended before its mutation.");
            return mutation;
        }
        var response = await scope.InvokeAsync(endpoint, service, method, request, cancellationToken: token);
        if (!command.GetProperty("content").GetBoolean()) return response;
        var reference = response.Deserialize<SunderRpcContentReference>(Json)!;
        if (reference.Length is < 1 or > 8 * 1024 * 1024 || reference.MediaType != "application/json"
            || reference.FileName != "builder-result.json" || reference.Repeatability != SunderRpcContentRepeatability.SingleUse
            || reference.ExpiresAtUtc > scope.DeadlineUtc)
            throw new InvalidDataException("Maker returned invalid scoped content metadata.");
        await using var content = await scope.OpenContentAsync(reference, token);
        var bytes = new byte[checked((int)reference.Length)];
        await content.ReadExactlyAsync(bytes, token);
        if (await content.ReadAsync(new byte[1], token) != 0 || reference.Sha256 != Convert.ToHexStringLower(SHA256.HashData(bytes)))
            throw new InvalidDataException("Maker returned truncated, extra or changed scoped content.");
        using var document = JsonDocument.Parse(bytes);
        return document.RootElement.Clone();
    }

    private static Task WriteResponseAsync<T>(string path, string id, T response)
        => WriteAsync(path, id, new { id, success = true, response });

    private static Task WriteFailureAsync(string path, string id, Exception error)
        => WriteAsync(path, id, new { id, success = false, error = error.ToString()[..Math.Min(error.ToString().Length, 32 * 1024)] });

    private static async Task WriteAsync<T>(string path, string id, T result)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(result, Json);
        if (bytes.Length > 16 * 1024 * 1024) throw new InvalidDataException("Native App response exceeds its bound.");
        await File.WriteAllBytesAsync(path + ".tmp", bytes);
        File.Move(path + ".tmp", path, overwrite: true);
        Console.Write("SUNDER_NATIVE_APP_RPC:" + id + "\n");
        Console.Out.Flush();
    }
}
