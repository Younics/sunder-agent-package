extern alias OpenAiApp;

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Rpc;
using Xunit;
using IManagementService = OpenAiApp::Sunder.Generated.Packages.Sunder_Package_Agent_Provider_Openai_Services.IManagementService;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed partial class OpenAiManagementSourceTests
{
    [OpenAiSourceFact]
    public async Task SourceManagement_PreservesFrozenIdentityAndRuntimeOwnedCredentialsAndAuth()
    {
        var repository = FindRepository();
        const string contractId = "sunder.package.agent.provider.openai.management";
        using var frozen = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "compatibility/agent-rpc-migration-baseline.json")));
        var expected = Assert.Single(frozen.RootElement.GetProperty("descriptors").EnumerateArray(),
            entry => entry.GetProperty("contractId").GetString() == contractId);
        var owned = JsonNode.Parse(File.ReadAllBytes(Path.Combine(repository,
            "src/Sunder.Package.Agent.Provider.OpenAI/sunder-contracts.owned.json")))!;
        var current = Assert.Single(owned["retiredPrivateContracts"]!.AsObject(),
            entry => entry.Value!["contractId"]!.GetValue<string>() == contractId).Value!["current"]!;
        Assert.Equal("release-frozen", current["state"]!.GetValue<string>());
        var historical = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(current["canonicalDescriptor"]!.GetValue<string>()));
        Assert.Equal(Convert.FromBase64String(expected.GetProperty("canonicalDescriptorBase64").GetString()!), historical.GetCanonicalUtf8());
        Assert.Equal(expected.GetProperty("descriptorSha256").GetString(), historical.Sha256);
        Assert.Equal(expected.GetProperty("abiSha256").GetString(), historical.AbiSha256);
        var configuration = typeof(OpenAiManagementService).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "src/Sunder.Package.Agent.Provider.OpenAI/obj",
            configuration, "net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc/authoring-receipt.v1.json")));
        var emitted = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray(), item => item.GetProperty("contractId").GetString() == contractId);
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(emitted.GetProperty("canonicalDescriptor").GetString()!));
        using var successors = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(repository, "compatibility/agent-ordinary-private-successors.json")));
        var successor = Assert.Single(successors.RootElement.GetProperty("contracts").EnumerateArray(), item => item.GetProperty("contractId").GetString() == contractId);
        Assert.Equal(successor.GetProperty("version").GetString(), descriptor.Version);
        Assert.Equal(successor.GetProperty("descriptorSha256").GetString(), descriptor.Sha256);
        Assert.Equal(successor.GetProperty("abiSha256").GetString(), descriptor.AbiSha256);
        var app = typeof(IManagementService).Assembly;
        Assert.NotSame(typeof(OpenAiManagementService).Assembly, app);
        Assert.DoesNotContain(app.GetReferencedAssemblies(), reference => reference.Name is "Sunder.Package.Agent.Provider.OpenAI" or "Sunder.Sdk.Worker");
        await Verify(descriptor);
    }

    private static async Task Verify(SunderRpcContractDescriptor descriptor)
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        using var auth = new CodexConnectedAuthStrategy(context);
        var runtime = new OpenAiManagementService(new OpenAiAuthOperationHandler(auth), new ProviderCredentialAccessor(context.Secrets, "auth.apiKey"));
        var registration = runtime.CreateSunderRpcProviderRegistration();
        Assert.Equal(descriptor.ContractId, registration.ContractId);
        Assert.Equal(descriptor.Version, registration.ContractVersion);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        var rpc = new Loopback(registration, descriptor);
        var services = new ServiceCollection();
        services.AddSingleton<ISunderRpcClient>(rpc);
        GeneratedProviderComposition.App(typeof(IManagementService).Assembly).ConfigureAppServices(services, context);
        await using var serviceProvider = services.BuildServiceProvider();
        var client = serviceProvider.GetRequiredService<IManagementService>();
        var disconnected = await client.GetAuthStatusAsync();
        Assert.False(disconnected.IsConnected); Assert.False(disconnected.HasCachedSession); Assert.Null(disconnected.ExpiresAtUtc);
        var session = new OpenAiCodexSession("local-access-token", "local-refresh-token", DateTimeOffset.UtcNow.AddHours(1), "local-account");
        await new CodexSessionStore(context.Secrets).SaveAsync(session);
        var connected = await client.GetAuthStatusAsync();
        Assert.True(connected.IsConnected); Assert.True(connected.HasCachedSession); Assert.Equal(session.ExpiresAtUtc, connected.ExpiresAtUtc);
        Assert.DoesNotContain("local-access-token", rpc.LastResponse.GetRawText(), StringComparison.Ordinal);
        Assert.False(await client.GetCredentialStatusAsync());
        Assert.True(await client.SetCredentialAsync("  source-api-key  "));
        Assert.Equal("source-api-key", await context.Secrets.GetSecretAsync("auth.apiKey"));
        var invalid = await Assert.ThrowsAsync<SunderRpcException>(() => client.SetCredentialAsync("   ").AsTask());
        Assert.Equal("openai.management.failed", invalid.Error.Code);
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ClearCredentialAsync(cancelled.Token).AsTask());
            Assert.True(await client.GetCredentialStatusAsync());
        }
        Assert.False((await client.DisconnectAuthAsync()).IsConnected);
        Assert.Null(await new CodexSessionStore(context.Secrets).GetAsync());
        Assert.False(await client.ClearCredentialAsync());
        Assert.False(await client.ClearCredentialAsync());
        Assert.Equal(rpc.ScopesCreated, rpc.ScopesDisposed);
        rpc.Available = false;
        var unavailable = await Assert.ThrowsAsync<SunderRpcException>(() => client.GetAuthStatusAsync().AsTask());
        Assert.Equal("rpc.provider-not-found", unavailable.Error.Code);
    }

    internal static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.AgentPackage.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Agent repository root was not found.");
    }

}

internal sealed class OpenAiSourceFactAttribute : FactAttribute
{
    public OpenAiSourceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION") == "1") return;
        if (!File.Exists(Path.GetFullPath(Path.Combine(OpenAiManagementSourceTests.FindRepository(), "../sunder-core/Sunder.Core.slnx"))))
            Skip = "Requires sibling Core source/tooling. Set SUNDER_REQUIRE_AGENT_SOURCE_MIGRATION=1 to require this lane.";
    }
}
