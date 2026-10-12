using System.Reflection;
using System.Runtime.Loader;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Runs against the ordinary compiler-built, imported-provider fixture. Reflection
// controls its BCL gates only; generated services own every RPC call and reference.
internal static class GeneratedReferenceParticipantProbe
{
    internal static async Task VerifyAsync(string path)
    {
        foreach (var observed in new[] { false, true })
            foreach (var completion in new[] { "target-retired", "caller-cancelled", "receiver-disposed", "completed" })
                await VerifyAsync(path, observed, completion);
    }

    internal static async Task VerifyAsync(string path, bool observed, string completion)
    {
        var load = new AssemblyLoadContext("controlled-natural-providers", isCollectible: true);
        load.Resolving += (_, name) => AssemblyLoadContext.Default.Assemblies
            .FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name));
        try
        {
            var module = load.LoadFromAssemblyPath(path);
            Assert.DoesNotContain(module.GetReferencedAssemblies(), name => name.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal));
            await VerifyAsync(module, observed, completion);
        }
        finally { load.Unload(); }
    }

    private static async Task VerifyAsync(Assembly module, bool observed, string completion)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var targetReceiver = new GeneratedServiceTestScope(rpc.ForCaller("example.controlled.target"));
        await using var toolReceiver = new GeneratedServiceTestScope(rpc.ForCaller("example.controlled.tools"));
        await using var consumer = new GeneratedServiceTestScope(rpc);
        var target = Activator.CreateInstance(module.GetType("ControlledNaturalProviders.ControlledTarget", true)!)!;
        var tools = Activator.CreateInstance(module.GetType("ControlledNaturalProviders.ControlledStrictReferenceService", true)!)!;
        var targetRegistration = targetReceiver.Register(target, RegressionTestBindings.ExecutionTargets);
        const string toolContract = "example.agent.controlled.strict.reference";
        var toolRegistration = toolReceiver.Register(tools, toolContract);
        state.AddGenerated("example.controlled.target", target, targetRegistration, Noop.Instance, targetRegistration.ProviderId);
        var strictDescriptor = Assert.Single(module.GetTypes().Where(type => !type.ContainsGenericParameters)
            .SelectMany(type => type.GetProperties(BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic))
            .Where(property => property.PropertyType == typeof(SunderRpcContractDescriptor))
            .Select(property => (SunderRpcContractDescriptor)property.GetValue(null)!),
            descriptor => descriptor.ContractId == toolContract);
        state.Add("example.controlled.tools", strictDescriptor, tools, toolRegistration.Handler,
            Noop.Instance, toolRegistration.ProviderId, "2.0.0");
        var selected = Assert.Single((await consumer.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var context = new AgentToolExecutionContext(Guid.NewGuid())
        {
            ExecutionTarget = selected.Reference,
            ExecutionTargetConfigurationGeneration = "fixture.configuration.1",
            ApprovedResourceCapabilities = ["fixture.capability"],
        };
        var pending = InvokeAsync();
        try
        {
            var started = Signal("Started");
            await Task.WhenAny(started, pending).WaitAsync(TimeSpan.FromSeconds(5));
            if (!started.IsCompleted) await pending;
            await started.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(0, Describes());
            if (!observed)
            {
                Assert.Equal(context.ExecutionTargetConfigurationGeneration, tools.GetType().GetProperty("ConfigurationGeneration")!.GetValue(tools));
                Assert.Equal(context.ApprovedResourceCapabilities, (IReadOnlyList<string>)tools.GetType().GetProperty("ResourceCapabilities")!.GetValue(tools)!);
            }

            Task? stop = null;
            switch (completion)
            {
                case "target-retired": await state.RetireAsync(target); break;
                case "caller-cancelled": await cancellation.CancelAsync(); break;
                case "receiver-disposed": stop = consumer.DisposeAsync().AsTask(); break;
                default: tools.GetType().GetMethod("Release")!.Invoke(tools, null); break;
            }
            if (completion == "completed")
            {
                Assert.Equal("fixture.target", await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.Equal(1, Describes());
            }
            else
            {
                var failure = await Record.ExceptionAsync(async () => await pending.WaitAsync(TimeSpan.FromSeconds(5)));
                Assert.True(failure is OperationCanceledException || failure is SunderRpcException
                    { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Cancelled or SunderRpcErrorKind.Unavailable },
                    $"{(observed ? "observed" : "unary")}/{completion}: {failure}");
                await Signal("Cancelled").WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(0, Describes());
            }
            await Signal("Exited").WaitAsync(TimeSpan.FromSeconds(5));
            if (stop is not null) await stop.WaitAsync(TimeSpan.FromSeconds(5));
            if (completion == "target-retired")
            {
                Assert.True(selected.RetirementToken.IsCancellationRequested);
                var stale = await Assert.ThrowsAsync<SunderRpcException>(() => selected.Reference.EnsureCurrentAsync().AsTask());
                Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
            }
        }
        finally { await cancellation.CancelAsync(); }

        int Describes() => (int)target.GetType().GetProperty("Describes")!.GetValue(target)!;
        Task Signal(string name) => (Task)tools.GetType().GetProperty(name)!.GetValue(tools)!;
        async Task<string> InvokeAsync()
        {
            var contract = module.GetType("Sunder.Generated.Packages.Example_Agent_Controlled_Services.IStrictReferenceService", true)!;
            var service = await (Task<object>)typeof(GeneratedReferenceParticipantProbe)
                .GetMethod(nameof(SelectServiceAsync), BindingFlags.Static | BindingFlags.NonPublic)!
                .MakeGenericMethod(contract).Invoke(null, [consumer])!;
            var method = contract.GetMethod(observed ? "ObserveAsync" : "ExecuteAsync")!;
            var requestType = method.GetParameters()[0].ParameterType;
            var request = Activator.CreateInstance(requestType)!;
            var targetProperty = requestType.GetProperty("Target")!;
            Assert.Null(targetProperty.GetCustomAttribute<SunderOptionalServiceReferenceAttribute>());
            targetProperty.SetValue(request, await consumer.RebindReferenceAsync(selected.Reference,
                targetProperty.PropertyType.GenericTypeArguments[0]));
            requestType.GetProperty("ConfigurationGeneration")!.SetValue(request, context.ExecutionTargetConfigurationGeneration);
            requestType.GetProperty("ResourceCapabilities")!.SetValue(request, context.ApprovedResourceCapabilities);
            if (!observed)
            {
                return await (ValueTask<string>)method.Invoke(service, [request, cancellation.Token])!;
            }
            var stream = (IAsyncEnumerable<string>)method.Invoke(service, [request, cancellation.Token])!;
            await using var iterator = stream.GetAsyncEnumerator(cancellation.Token);
            Assert.True(await iterator.MoveNextAsync());
            Assert.Equal("dispatched", iterator.Current);
            Assert.True(await iterator.MoveNextAsync());
            return iterator.Current;
        }
    }

    private static async Task<object> SelectServiceAsync<T>(GeneratedServiceTestScope receiver) where T : class
        => Assert.Single((await receiver.Providers<T>().ListAsync()).Providers).Service;

    private sealed class Noop : IDisposable
    {
        internal static Noop Instance { get; } = new();
        public void Dispose() { }
    }
}
