using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    internal static async Task VerifyAgentPromptContext(Assembly owner)
    {
        await VerifyAgentPromptMapping(owner);
        await VerifyAgentPromptAuthority(owner);
    }

    private static async Task VerifyAgentPromptMapping(Assembly owner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        var observed = new FilesRpcObserver(rpc, "contribute");
        var receiver = new GeneratedServiceTestScope(observed);
        using var providers = new RegressionTestSourceProviders();
        var peer = new AgentPromptPeer();
        var registration = providers.Create(RegressionTestBindings.PromptContextContributors, peer, receiver);
        var handler = AddSurfaceProvider(state, "younics.ai.agent", registration);
        var client = Assert.Single((await receiver.Providers<AgentServices.IPromptContextContributorService>().ListAsync()).Providers).Service;
        var description = await client.DescribeAsync();
        Assert.Equal(peer.ContributorId, description.ContributorId); Assert.Equal(peer.DisplayName, description.DisplayName);
        Assert.Equal(peer.AcknowledgmentSinkId, description.AcknowledgmentSinkId);
        var request = AgentPublicSourceMigrationTests.CreatePromptRequest().DomainRequestWithoutTarget();
        foreach (var result in new AgentPromptContextContribution?[] { null, new([]), new([AgentPublicSourceMigrationTests.RichPromptBlock(), new("Empty", "", int.MinValue)]) })
        {
            peer.Result = result;
            var received = await client.ContributeAsync(request);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(result, MemoryProviderJson), JsonSerializer.SerializeToElement(received, MemoryProviderJson)));
            var expectedWire = CurrentPromptRequestWire(request);
            var actualWire = handler.Calls.Last(call => call.Method == "contribute").Request;
            Assert.True(JsonElement.DeepEquals(expectedWire, actualWire), string.Join(Environment.NewLine,
                PromptWireDifferences(expectedWire, actualWire, "$")));
            Assert.True(JsonElement.DeepEquals(expectedWire, CurrentPromptRequestWire(peer.Request!)));
            // Turn exposes interface ports; compare their borrowed originals in
            // the same assertion-side value projection as the concrete records.
            Assert.Equal(peer.Request!.Session, AgentRoleBoundary.As<AgentSessionContextRecord>(peer.Request.Turn.Session));
            Assert.Equal(peer.Request.Run, AgentRoleBoundary.As<AgentRunContextRecord>(peer.Request.Turn.Run));
        }
        var target = new AgentPublicSourceMigrationTests.PromptTargetProbe();
        var targetReceiver = new GeneratedServiceTestScope(rpc);
        var targetRegistration = providers.Create(RegressionTestBindings.ExecutionTargets, target, targetReceiver);
        var targetHandler = new SurfaceHandler(targetRegistration.Handler);
        state.Add("example.prompt.target", CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets), targetHandler, targetHandler, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
        var describes = target.Describes; // Registration inspects the target's facet metadata.
        var reference = Assert.Single((await receiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers).Reference;
        request = request with { ExecutionTarget = reference };
        await client.ContributeAsync(request);
        Assert.True(JsonElement.DeepEquals(PromptTargetLocator(reference),
            PromptTargetLocator(Assert.IsType<ServiceReference<AgentServices.IExecutionTargetService>>(peer.Request!.ExecutionTarget))));
        Assert.True(JsonElement.DeepEquals(PromptTargetLocator(reference),
            handler.Calls.Last(call => call.Method == "contribute").Request.GetProperty("executionTarget")));
        Assert.Equal(describes, target.Describes); // Forwarding the handle must not activate an execution client.
        await state.RetireAsync(targetHandler);
        state.Add("example.prompt.target", CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ExecutionTargets), targetHandler, targetHandler, Noop.Instance, targetRegistration.ProviderId, "2.0.0");
        await client.ContributeAsync(request); Assert.Null(peer.Request!.ExecutionTarget);
        Assert.Equal(describes, target.Describes);
        request = request with { Profile = null, Workspace = null, ExecutionBinding = null, MemoryConsistencyBarrier = null, ExecutionTarget = null,
            AvailableTools = [], Turns = [], RecentLiveBufferTurns = [], ContextPlan = AgentPromptContextPlan.None(), TranscriptEpoch = 0 };
        await client.ContributeAsync(request);
        var minimal = handler.Calls.Last(call => call.Method == "contribute").Request;
        foreach (var field in new[] { "profile", "workspace", "executionBinding", "memoryConsistencyBarrier", "executionTarget" })
        {
            Assert.True(minimal.TryGetProperty(field, out var value));
            Assert.Equal(JsonValueKind.Null, value.ValueKind);
        }
        Assert.False(peer.Request!.ContextPlan.ShouldContribute());
        var validGeneratedRequest = state.LastInvocationRequest;
        var contributions = peer.Contributions;
        var attempts = state.InvocationAttemptCount;
        await Assert.ThrowsAsync<JsonException>(() => client.ContributeAsync(request with
        {
            ContextPlan = request.ContextPlan with { MaxChars = -1 },
        }).AsTask());
        Assert.Equal(attempts, state.InvocationAttemptCount);
        Assert.Equal(contributions, peer.Contributions);
        var invalidWire = JsonNode.Parse(validGeneratedRequest.GetRawText())!.AsObject();
        invalidWire["contextPlan"]!["maxChars"] = -1;
        var source = Assert.Single(state.Discover(RegressionTestBindings.PromptContextContributors).Providers,
            value => value.ProviderId == registration.ProviderId).Endpoint;
        var remote = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.InvokeAsync(source,
            "prompt-context-contributor", "contribute", JsonSerializer.SerializeToElement(invalidWire)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, remote.Error.Kind);
        Assert.Equal(attempts + 1, state.InvocationAttemptCount);
        Assert.Equal(contributions, peer.Contributions);
        var receipt = new AgentPromptContextReceipt(request.Session.SessionId, request.Run.RunId, request.TranscriptEpoch,
            [new("host.identity", new string('b', 64), "/workspace/AGENTS.md", new string('a', 64))]);
        await client.AcknowledgeAsync(receipt);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(receipt, MemoryProviderJson), JsonSerializer.SerializeToElement(peer.Receipt, MemoryProviderJson)));
        var acknowledgments = peer.Acknowledgments;
        var validGeneratedReceipt = state.LastInvocationRequest;
        attempts = state.InvocationAttemptCount;
        await Assert.ThrowsAsync<JsonException>(() => client.AcknowledgeAsync(
            receipt with { Blocks = [receipt.Blocks[0] with { ContentHash = "short" }] }).AsTask());
        Assert.Equal(attempts, state.InvocationAttemptCount);
        Assert.Equal(acknowledgments, peer.Acknowledgments);
        var invalidReceiptWire = JsonNode.Parse(validGeneratedReceipt.GetRawText())!.AsObject();
        invalidReceiptWire["blocks"]![0]!["contentHash"] = "short";
        var invalidReceipt = await Assert.ThrowsAsync<SunderRpcException>(() => rpc.InvokeAsync(source,
            "prompt-context-contributor", "acknowledge", JsonSerializer.SerializeToElement(invalidReceiptWire)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, invalidReceipt.Error.Kind);
        Assert.Equal(attempts + 1, state.InvocationAttemptCount);
        Assert.Equal(acknowledgments, peer.Acknowledgments);
        foreach (var change in new Func<SunderRpcProviderSnapshot, SunderRpcProviderSnapshot>[]
        {
            value => value with { PackageId = "other.package" }, value => value with { PackageVersion = "2.0.1" },
            value => value with { ProviderId = "other.provider" }, value => value with { ContractId = "other.contract" },
            value => value with { ContractVersion = "1.0.1" }, value => value with { DescriptorSha256 = new string('c', 64) },
            value => value with { AbiSha256 = new string('d', 64) }, value => value with { ActivationId = Guid.NewGuid() },
            value => value with { ActivationEpoch = value.ActivationEpoch + 1 }, value => value with { SessionGeneration = value.SessionGeneration + 1 },
            value => value with { Endpoint = new("other-endpoint") }, value => value with { State = SunderRpcProviderState.Inactive },
        })
        {
            var (tamperedReceiver, tamperedClient) = await SelectAsync();
            observed.Transform = change; var calls = handler.Calls.Count;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => tamperedClient.ContributeAsync(request).AsTask())).Error.Kind);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => tamperedClient.AcknowledgeAsync(receipt).AsTask())).Error.Kind);
            Assert.Equal(calls, handler.Calls.Count);
            observed.Transform = null;
            await tamperedReceiver.DisposeAsync();
        }
        var (invalidatedReceiver, invalidatedClient) = await SelectAsync();
        observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => invalidatedClient.ContributeAsync(request).AsTask())).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;
        await invalidatedReceiver.DisposeAsync();
        var (failureReceiver, failureClient) = await SelectAsync();
        observed.LookupFailure = SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => failureClient.ContributeAsync(request).AsTask())).Error.Kind);
        observed.LookupFailure = null;
        await failureReceiver.DisposeAsync();
        foreach (var method in new[] { "contribute", "acknowledge" })
        {
            var (cancellationReceiver, cancellationClient) = await SelectAsync();
            Task Call(CancellationToken token) => method == "contribute" ? cancellationClient.ContributeAsync(request, token).AsTask() : cancellationClient.AcknowledgeAsync(receipt, token).AsTask();
            using (var cancelled = new CancellationTokenSource())
            {
                cancelled.Cancel(); var calls = handler.Calls.Count;
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Call(cancelled.Token)); Assert.Equal(calls, handler.Calls.Count);
            }
            handler.BlockMethod = method; handler.Gate = new();
            using (var cancelled = new CancellationTokenSource())
            {
                var pending = Call(cancelled.Token); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                cancelled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await handler.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            }
            handler.Gate = null; handler.FailMethod = method;
            var error = await Assert.ThrowsAsync<SunderRpcException>(() => Call(default));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind); Assert.DoesNotContain("private-surface", error.Message, StringComparison.Ordinal);
            handler.FailMethod = null;
            await cancellationReceiver.DisposeAsync();
        }
        handler.BlockMethod = "contribute"; handler.Gate = new();
        var (retiringReceiver, retiringClient) = await SelectAsync();
        var retiring = retiringClient.ContributeAsync(request).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        var retired = await Assert.ThrowsAsync<SunderRpcException>(() => retiring);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, retired.Error.Kind);
        await retiringReceiver.DisposeAsync();
        handler = AddSurfaceProvider(state, "younics.ai.agent", registration);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.AcknowledgeAsync(receipt).AsTask())).Error.Kind);
        var (nextReceiver, replacement) = await SelectAsync();
        await replacement.ContributeAsync(request);
        handler.BlockMethod = "acknowledge"; handler.Gate = new();
        var stopping = replacement.AcknowledgeAsync(receipt).AsTask();
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextReceiver.DisposeAsync().AsTask(); Assert.Same(stopped, nextReceiver.DisposeAsync().AsTask());
        var stoppedCall = await Assert.ThrowsAsync<SunderRpcException>(() => stopping);
        Assert.Equal(SunderRpcErrorKind.Unavailable, stoppedCall.Error.Kind);
        Assert.Equal("rpc.binding.retired", stoppedCall.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, stoppedCall.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, stoppedCall.Error.RetrySafety);
        await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => replacement.DescribeAsync().AsTask());
        await targetReceiver.DisposeAsync();
        await receiver.DisposeAsync();
        observed.AssertScopedOperations(); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));

        async Task<(GeneratedServiceTestScope Scope, AgentServices.IPromptContextContributorService Service)> SelectAsync()
        {
            var scope = new GeneratedServiceTestScope(observed);
            try
            {
                var service = Assert.Single((await scope.Providers<AgentServices.IPromptContextContributorService>().ListAsync()).Providers).Service;
                return (scope, service);
            }
            catch
            {
                await scope.DisposeAsync();
                throw;
            }
        }
    }

    private static JsonElement CurrentPromptRequestWire(AgentPromptContextRequest request)
    {
        // Independent scenario projection of the reviewed current request shape.
        // Keep the full Turn snapshot, offset-bearing timestamps and required nulls.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false) },
        };
        return JsonSerializer.SerializeToElement(new
        {
            request.Session, request.Run, request.Turn, request.Turns, request.RecentLiveBufferTurns,
            request.ContextPlan, request.Profile, request.Workspace, request.ExecutionBinding,
            request.AvailableTools, request.MemoryConsistencyBarrier, request.TranscriptEpoch,
            ExecutionTarget = request.ExecutionTarget is null ? (JsonElement?)null : PromptTargetLocator(request.ExecutionTarget),
        }, options);
    }

    private static JsonElement PromptTargetLocator(ServiceReference<AgentServices.IExecutionTargetService> reference)
    {
        var snapshot = (SunderRpcProviderSnapshot)reference.GetType()
            .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reference)!;
        // The reviewed SDK locator has these eleven identity members, not the
        // catalog-only State/Revision members and not a serialized service object.
        return JsonSerializer.SerializeToElement(new
        {
            snapshot.PackageId, snapshot.PackageVersion, snapshot.ProviderId, snapshot.ContractId,
            snapshot.ContractVersion, snapshot.DescriptorSha256, snapshot.AbiSha256, snapshot.ActivationId,
            snapshot.ActivationEpoch, snapshot.SessionGeneration, Endpoint = snapshot.Endpoint.Value,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    private static IEnumerable<string> PromptWireDifferences(JsonElement expected, JsonElement actual, string path)
    {
        if (JsonElement.DeepEquals(expected, actual)) yield break;
        if (expected.ValueKind == JsonValueKind.Object && actual.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in expected.EnumerateObject())
            {
                if (!actual.TryGetProperty(property.Name, out var value))
                    yield return $"{path}.{property.Name}: expected {property.Value.GetRawText()}; actual <missing>";
                else
                    foreach (var difference in PromptWireDifferences(property.Value, value, path + "." + property.Name))
                        yield return difference;
            }
            foreach (var property in actual.EnumerateObject())
                if (!expected.TryGetProperty(property.Name, out _))
                    yield return $"{path}.{property.Name}: expected <missing>; actual {property.Value.GetRawText()}";
        }
        else if (expected.ValueKind == JsonValueKind.Array && actual.ValueKind == JsonValueKind.Array)
        {
            if (expected.GetArrayLength() != actual.GetArrayLength())
                yield return $"{path}.Length: expected {expected.GetArrayLength()}; actual {actual.GetArrayLength()}";
            for (var index = 0; index < Math.Min(expected.GetArrayLength(), actual.GetArrayLength()); index++)
                foreach (var difference in PromptWireDifferences(expected[index], actual[index], $"{path}[{index}]"))
                    yield return difference;
        }
        else yield return $"{path}: expected {expected.GetRawText()}; actual {actual.GetRawText()}";
    }

    private sealed class AgentPromptPeer : IAgentPromptContextContributor, IAgentPromptContextAcknowledgmentSink
    {
        public string ContributorId { get; init; } = "fixture.prompt.context";
        public string DisplayName => "Prompt fixture";
        public string AcknowledgmentSinkId => "fixture.prompt.receipts";
        internal AgentPromptContextRequest? Request;
        internal AgentPromptContextReceipt? Receipt;
        internal int Contributions;
        internal int Acknowledgments;
        internal AgentPromptContextContribution? Result = new([AgentPublicSourceMigrationTests.RichPromptBlock()]);
        public ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Contributions++; Request = request; return ValueTask.FromResult(Result); }
        public ValueTask AcknowledgePromptContextAsync(AgentPromptContextReceipt receipt, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Receipt = receipt; Acknowledgments++; return ValueTask.CompletedTask; }
    }
}
