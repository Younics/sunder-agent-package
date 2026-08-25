using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class TestSupportContractTests
{
    [Fact]
    public async Task InMemorySettings_DistinguishesStoredEffectiveDefaultAndSchemaBehavior()
    {
        var schema = new PackageSettingsSchema(
            "Test schema",
            [
                new PackageSettingsSection(
                    "general",
                    "General",
                    null,
                    [
                        new PackageSettingsField("mode", "Mode", PackageSettingsFieldKind.Select,
                            defaultValue: "safe",
                            options: [new("safe", "Safe"), new("fast", "Fast")]),
                        new PackageSettingsField("enabled", "Enabled", PackageSettingsFieldKind.Boolean),
                        new PackageSettingsField("required", "Required", PackageSettingsFieldKind.Text, isRequired: true),
                        new PackageSettingsField("secret", "Secret", PackageSettingsFieldKind.Secret),
                    ]),
            ]);
        var settings = new InMemoryPackageSettings(schema: schema);

        Assert.Equal("safe", await settings.GetValueAsync("mode"));
        Assert.Null(await settings.GetStoredValueAsync("mode"));

        await settings.SetValueAsync("mode", "fast");
        Assert.Equal("fast", await settings.GetValueAsync("mode"));
        Assert.Equal("fast", await settings.GetStoredValueAsync("mode"));
        await settings.DeleteValueAsync("mode");
        Assert.Equal("safe", await settings.GetValueAsync("mode"));
        Assert.Null(await settings.GetStoredValueAsync("mode"));

        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetValueAsync("mode", "unknown"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.SetValueAsync("enabled", "sometimes"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.DeleteValueAsync("required"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.GetValueAsync("secret"));
        await Assert.ThrowsAsync<ArgumentException>(() => settings.GetValueAsync("undeclared"));
    }

    [Fact]
    public async Task RegressionRpcClient_HonorsDirectAndScopedInvocationDeadlines()
    {
        using var state = new RegressionTestRpcState();
        var handler = new BlockingRpcHandler();
        state.Add(
            "test.provider",
            AgentRpcContractDescriptors.Get(AgentRpcContractIds.RuntimeCatalog),
            handler,
            handler,
            new CancellationTokenSource());
        var endpoint = Assert.Single(state.Discover(AgentRpcContractIds.RuntimeCatalog).Providers).Endpoint;
        var client = new RegressionTestRpcClient(state, "test.caller");
        await using var scope = await client.CreateCallScopeAsync(
            new SunderRpcCallOptions(DateTimeOffset.UtcNow.AddMinutes(1)));
        var request = JsonSerializer.SerializeToElement(new { });
        SunderRpcCallOptions Deadline() => new(DateTimeOffset.UtcNow.AddMilliseconds(100));
        var calls = new Func<Task>[]
        {
            () => client.InvokeAsync(endpoint, "runtime-catalog", "list-sessions", request, Deadline()).AsTask(),
            () => DrainAsync(client.SubscribeAsync(endpoint, "runtime-catalog", "list-sessions", request, Deadline())),
            () => scope.InvokeAsync(endpoint, "runtime-catalog", "list-sessions", request, Deadline()).AsTask(),
            () => DrainAsync(scope.SubscribeAsync(endpoint, "runtime-catalog", "list-sessions", request, Deadline())),
        };

        foreach (var call in calls)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => call().WaitAsync(TimeSpan.FromSeconds(2)));
        }
    }

    private static async Task DrainAsync(IAsyncEnumerable<JsonElement> values)
    {
        await foreach (var _ in values)
        {
        }
    }

    private sealed class BlockingRpcHandler : ISunderRpcServiceHandler
    {
        public async ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return request;
        }

        public async IAsyncEnumerable<JsonElement> InvokeServerStreamAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            yield return request;
        }
    }
}
