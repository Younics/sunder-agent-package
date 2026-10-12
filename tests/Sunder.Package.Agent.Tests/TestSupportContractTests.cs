using System.Runtime.CompilerServices;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Settings;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class TestSupportContractTests
{
    [Fact]
    public async Task GeneratedAuthorityProjection_PreservesDenialAndRequiresOwningContract()
    {
        using var roles = new CanonicalPackageRoles();
        var worker = roles.Load("Execution.Local", "worker");
        var target = worker.GetType(
            "Sunder.Generated.Packages.Sunder_Package_Agent_Models.AgentResourceAuthorityValidation", true)!;
        var candidate = Environment.GetEnvironmentVariable("SUNDER_AGENT_PUBLIC_FIXTURE_CANDIDATE")!;
        Assert.False(string.IsNullOrWhiteSpace(candidate));
        using var state = new RegressionTestRpcState();
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "test.receiver"));
        var denied = new Sunder.Package.Agent.Contracts.Contracts.AgentResourceAuthorityValidation(
            false, "resource-authority-retired", "The original authority was retired.");
        var execution = RegressionTestGeneratedProviderProjection.FromCandidate(candidate, RegressionTestBindings.ExecutionTargets);
        var projected = await execution.ProjectAsync(denied, target, receiver, CancellationToken.None);
        Assert.NotNull(projected);
        Assert.Equal(false, target.GetProperty("IsValid")!.GetValue(projected));
        Assert.Equal(denied.ErrorCode, target.GetProperty("ErrorCode")!.GetValue(projected));
        Assert.Equal(denied.ErrorMessage, target.GetProperty("ErrorMessage")!.GetValue(projected));

        var chat = RegressionTestGeneratedProviderProjection.FromCandidate(candidate, RegressionTestBindings.ChatProviders);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            chat.ProjectAsync(denied, target, receiver, CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task GeneratedReferenceProjection_PreservesReceiverBindingAndRejectsReplacementActivation()
    {
        using var roles = new CanonicalPackageRoles();
        var worker = roles.Load("Agent", "worker");
        using var state = new RegressionTestRpcState();
        var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ToolSources);
        var original = new BlockingRpcHandler();
        state.Add("test.package", descriptor, original, original, new CancellationTokenSource(), "test.tools", "2.0.0");
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "test.receiver"));
        var sourceType = worker.GetType(typeof(AgentServices.IToolSourceService).FullName!, true)!;
        Assert.NotEqual(typeof(AgentServices.IToolSourceService), sourceType);
        var catalog = receiver.Providers(sourceType);
        var snapshot = (await AgentRoleBoundary.Await(AgentRoleBoundary.Call(catalog, "ListAsync", CancellationToken.None)!)
            .WaitAsync(TimeSpan.FromSeconds(5)))!;
        var entries = (System.Collections.IEnumerable)snapshot.GetType().GetProperty("Providers")!.GetValue(snapshot)!;
        var source = Assert.Single(entries.Cast<object>());
        var reference = source.GetType().GetProperty("Reference")!.GetValue(source)!;
        var target = typeof(AgentServices.IToolSourceService);
        var projected = Assert.IsType<ServiceReference<AgentServices.IToolSourceService>>(
            await receiver.RebindReferenceAsync(reference, target));
        var repeated = Assert.IsType<ServiceReference<AgentServices.IToolSourceService>>(
            await receiver.RebindReferenceAsync(reference, target));
        // The SDK may issue another locator wrapper; the exact client/binding and
        // retirement lifetime, rather than wrapper allocation, must be preserved.
        Assert.Same(projected.Service, repeated.Service);
        Assert.Equal(projected.RetirementToken, repeated.RetirementToken);
        Assert.Equal((CancellationToken)source.GetType().GetProperty("RetirementToken")!.GetValue(source)!, projected.RetirementToken);
        await projected.EnsureCurrentAsync();

        await using var foreign = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "test.other.receiver"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => foreign.RebindReferenceAsync(reference, target).AsTask());
        await Assert.ThrowsAsync<InvalidOperationException>(() => receiver.RebindReferenceAsync(reference,
            typeof(AgentServices.IChatProviderService)).AsTask());
        await projected.EnsureCurrentAsync();

        await state.RetireAsync(original).WaitAsync(TimeSpan.FromSeconds(5));
        var replacement = new BlockingRpcHandler();
        state.Add("test.package", descriptor, replacement, replacement, new CancellationTokenSource(), "test.tools", "2.0.0");
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => receiver.RebindReferenceAsync(reference, target).AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        Assert.True(projected.RetirementToken.IsCancellationRequested);
        Assert.False(replacement.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task RoleServiceAdapter_PreservesExactGeneratedServiceAndReceiverLifetime()
    {
        using var roles = new CanonicalPackageRoles();
        var worker = roles.Load("Agent", "worker");
        using var state = new RegressionTestRpcState();
        var descriptor = CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ToolSources);
        var handler = new BlockingRpcHandler();
        state.Add("test.package", descriptor, handler, handler, new CancellationTokenSource(), "test.tools", "2.0.0");
        await using var receiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent"));
        var contract = worker.GetType(typeof(AgentServices.IToolSourceService).FullName!, throwOnError: true)!;
        var catalog = receiver.Providers(contract);
        var snapshot = (await AgentRoleBoundary.Await(AgentRoleBoundary.Call(catalog, "ListAsync", CancellationToken.None)!)
            .WaitAsync(TimeSpan.FromSeconds(5)))!;
        var entries = (System.Collections.IEnumerable)snapshot.GetType().GetProperty("Providers")!.GetValue(snapshot)!;
        var entry = Assert.Single(entries.Cast<object>());
        var service = entry.GetType().GetProperty("Service")!.GetValue(entry)!;
        var adapted = AgentRoleBoundary.As<AgentServices.IToolSourceService>(service);
        Assert.NotSame(typeof(AgentServices.IToolSourceService).Assembly, service.GetType().Assembly);
        Assert.Same(service, AgentRoleBoundary.Unwrap(adapted));
        var invocation = adapted.ExecuteAsync(new AgentToolExecutionContext(Guid.NewGuid()),
            new AgentToolRequest("fixture.tool", "{}")).AsTask();
        try
        {
            await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopping = receiver.DisposeAsync().AsTask();
            Assert.Same(stopping, receiver.DisposeAsync().AsTask());
            var retired = await Assert.ThrowsAsync<SunderRpcException>(() => invocation.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Equal(SunderRpcErrorKind.Unavailable, retired.Error.Kind);
            Assert.Equal("rpc.binding.retired", retired.Error.Code);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, retired.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, retired.Error.RetrySafety);
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(((CancellationToken)entry.GetType().GetProperty("RetirementToken")!.GetValue(entry)!).IsCancellationRequested);
            var stopped = await Assert.ThrowsAsync<SunderRpcException>(() =>
                AgentRoleBoundary.Await(AgentRoleBoundary.Call(entry, "EnsureCurrentAsync", CancellationToken.None)!));
            Assert.Equal(SunderRpcErrorKind.Unavailable, stopped.Error.Kind);
            Assert.Equal("rpc.binding.lifetime-disposed", stopped.Error.Code);
        }
        finally
        {
            // Failure cleanup cannot depend on the receiver behavior being tested.
            await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

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
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RuntimeCatalog),
            handler,
            handler,
            new CancellationTokenSource());
        var endpoint = Assert.Single(state.Discover(RegressionTestBindings.RuntimeCatalog).Providers).Endpoint;
        var client = new RegressionTestRpcClient(state, "test.caller");
        await using var scope = await client.CreateCallScopeAsync(
            new SunderRpcCallOptions(DateTimeOffset.UtcNow.AddMinutes(1)));
        var request = JsonSerializer.SerializeToElement(new { });
        SunderRpcCallOptions Deadline() => new(DateTimeOffset.UtcNow.AddMilliseconds(100));
        var calls = new Func<Task>[]
        {
            () => client.InvokeAsync(endpoint, "runtime-catalog", "list-sessions", request, Deadline()).AsTask(),
            () => DrainAsync(client.SubscribeAsync(endpoint, "runtime-catalog", "watch", request, Deadline())),
            () => scope.InvokeAsync(endpoint, "runtime-catalog", "list-sessions", request, Deadline()).AsTask(),
            () => DrainAsync(scope.SubscribeAsync(endpoint, "runtime-catalog", "watch", request, Deadline())),
        };

        foreach (var call in calls)
        {
            var cancelled = await Assert.ThrowsAsync<TaskCanceledException>(
                () => call().WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(cancelled.CancellationToken.IsCancellationRequested);
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
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<JsonElement> InvokeUnaryAsync(
            SunderRpcInvocationContext context,
            string serviceId,
            string methodId,
            JsonElement request,
            CancellationToken cancellationToken)
        {
            Started.TrySetResult();
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
