using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class CredentialSourceMigrationTests
{
    private static readonly string[] Families = ["Gemini", "Anthropic", "LMStudio"];
    internal static readonly string[] SourcePaths = Families.SelectMany(family => new[]
    {
        $"src/Sunder.Package.Agent.Provider.{family}/Rpc/CredentialSettings.cs",
        $"src/Sunder.Package.Agent.Provider.{family}/App/{family}SettingsViewModel.cs",
    }).ToArray();

    [AgentPublicSourceMigrationFact]
    public async Task CredentialSource_MatchesCurrentContractAndRoleIsolatedSecretBehavior()
    {
        foreach (var family in Families)
        {
            using var roles = new CanonicalPackageRoles();
            var root = CanonicalPackageRoles.Source("Provider." + family);
            var packageId = AgentPackageRepositoryInventory.GetRuntimePackageId("Sunder.Package.Agent.Provider." + family);
            var descriptor = CanonicalPackageRoles.OwnedDescriptor(root, packageId + ".credentials");
            var baseline = CanonicalPackageRoles.ReadOwnedBaseline(root);
            CurrentPrivateRpcDescriptors.AssertCurrent(descriptor);
            var worker = roles.Load("Provider." + family, "worker");
            var app = roles.Load("Provider." + family, "app");
            Assert.Null(app.GetType($"Sunder.Package.Agent.Provider.{family}.ProviderCredentialOwnerRpcClient"));
            Assert.Null(app.GetType($"Sunder.Package.Agent.Provider.{family}.Rpc.CredentialSettingsService"));
            await VerifyBehavior(worker, app, descriptor, family, packageId);
            Assert.Equal(baseline, CanonicalPackageRoles.ReadOwnedBaseline(root));
        }
    }

    private static async Task VerifyBehavior(Assembly worker, Assembly app, SunderRpcContractDescriptor descriptor, string family, string packageId)
    {
        var secrets = new Secrets();
        var secretKey = family == "LMStudio" ? "connection.apiKey" : "auth.apiKey";
        var accessor = Activator.CreateInstance(worker.GetType("Sunder.Package.Agent.Provider.Shared.ProviderCredentialAccessor", true)!, secrets, secretKey)!;
        var type = worker.GetType($"Sunder.Package.Agent.Provider.{family}.Rpc.CredentialSettingsService", true)!;
        var instance = Activator.CreateInstance(type, accessor)!;
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(instance, null));
        Assert.Equal(family.ToLowerInvariant() + ".credentials", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, packageId);
        state.Add(packageId, descriptor, instance, registration.Handler, Noop.Instance, registration.ProviderId, "2.0.0");
        var endpoint = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers).Endpoint;
        using var scope = RegressionTestPackageScope.Create();
        var context = new TestPackageContext(packageId, scope.RootPath, version: "2.0.0", storage: scope.Context.Storage);
        await using var services = AgentSourceArtifactMigrationTests.CreateGeneratedApp(app, context, rpc);
        var serviceFamily = family == "LMStudio" ? "Lmstudio" : family;
        var api = app.GetType($"Sunder.Generated.Packages.Younics_Ai_Agent_Provider_{serviceFamily}_Services.ICredentialSettingsService", true)!;
        var client = services.GetRequiredService(api);
        var query = api.GetMethod("GetStatusAsync")!.CreateDelegate<Func<CancellationToken, ValueTask<bool>>>(client);
        var set = api.GetMethod("SetAsync")!.CreateDelegate<Func<string, CancellationToken, ValueTask<bool>>>(client);
        var clear = api.GetMethod("ClearAsync")!.CreateDelegate<Func<CancellationToken, ValueTask<bool>>>(client);
        Assert.False(await query(default));
        Assert.True(await set("  isolated-credential-漢字  ", default));
        Assert.Equal("isolated-credential-漢字", secrets.Value);
        Assert.True(await query(default));
        var status = await rpc.InvokeAsync(endpoint, "credential-settings", "get-status", JsonSerializer.SerializeToElement(new { }));
        Assert.DoesNotContain(secrets.Value!, status.GetRawText(), StringComparison.Ordinal);
        foreach (var blank in new[] { "", "   " })
        {
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => set(blank, default).AsTask());
            Assert.Equal("provider.credential.required", error.Error.Code);
            Assert.Equal("isolated-credential-漢字", secrets.Value);
        }
        // Natural methods reject omitted/wrong-kind input and unexpected clear
        // arguments at the generated boundary, before any secret mutation.
        foreach (var (method, request) in new[] { ("set", "{}"), ("set", "null"), ("clear", "{\"credential\":\"unexpected\"}") })
        {
            var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.InvokeAsync(endpoint,
                "credential-settings", method, JsonDocument.Parse(request).RootElement).AsTask());
            Assert.Equal(SunderRpcErrorKind.Validation, invalid.Error.Kind);
            Assert.Equal("isolated-credential-漢字", secrets.Value);
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => set("must-not-store", cancelled.Token).AsTask());
            Assert.Equal("isolated-credential-漢字", secrets.Value);
        }
        Assert.False(await clear(default));
        Assert.Null(secrets.Value);
        Assert.False(await clear(default));
        secrets.Fail = true;
        var failed = await Assert.ThrowsAsync<SunderRpcException>(() => query(default).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failed.Error.Kind);
        Assert.DoesNotContain("private secret storage", failed.Message, StringComparison.Ordinal);
        secrets.Fail = false;
        await state.RetireAsync(instance);
        var absent = await Assert.ThrowsAsync<SunderRpcException>(() => query(default).AsTask());
        Assert.Equal("rpc.provider-not-found", absent.Error.Code);
        Assert.All(secrets.Keys, key => Assert.Equal(secretKey, key));
    }

    private sealed class Secrets : IPackageSecrets
    {
        internal string? Value { get; private set; }
        internal bool Fail { get; set; }
        internal List<string> Keys { get; } = [];
        private void Check(string key, CancellationToken token)
        {
            token.ThrowIfCancellationRequested(); Keys.Add(key);
            if (Fail) throw new IOException("private secret storage failure");
        }
        public Task<string?> GetSecretAsync(string key, CancellationToken token = default) { Check(key, token); return Task.FromResult(Value); }
        public Task SetSecretAsync(string key, string value, CancellationToken token = default) { Check(key, token); Value = value; return Task.CompletedTask; }
        public Task DeleteSecretAsync(string key, CancellationToken token = default) { Check(key, token); Value = null; return Task.CompletedTask; }
    }
    private sealed class Noop : IDisposable { internal static Noop Instance { get; } = new(); public void Dispose() { } }
}
