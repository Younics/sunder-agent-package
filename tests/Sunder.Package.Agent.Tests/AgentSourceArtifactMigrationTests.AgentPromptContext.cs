using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
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
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        using var catalog = AgentRpcCatalog.CreateDormant(rpc);
        var peer = new AgentPromptPeer();
        var registration = SurfaceRegistration(AgentRoleBoundary.Create(owner.GetType("Sunder.Package.Agent.Rpc.Public.PromptContextContributorService", true)!, peer, catalog));
        var handler = AddSurfaceProvider(state, "sunder.package.agent", registration);
        var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.PromptContextContributor)).Providers);
        var observed = new FilesRpcObserver(rpc, "contribute");
        var client = CreateAgentPromptClient(owner, observed, snapshot);
        await using var lifetime = (IAsyncDisposable)client;
        var sink = (IAgentPromptContextAcknowledgmentSink)client;
        Assert.Equal(peer.ContributorId, client.ContributorId); Assert.Equal(peer.DisplayName, client.DisplayName);
        Assert.Equal(peer.AcknowledgmentSinkId, sink.AcknowledgmentSinkId);
        var request = AgentPublicSourceMigrationTests.CreatePromptRequest().DomainRequestWithoutTarget();
        foreach (var result in new AgentPromptContextContribution?[] { null, new([]), new([AgentPublicSourceMigrationTests.RichPromptBlock(), new("Empty", "", int.MinValue)]) })
        {
            peer.Result = result;
            var received = await client.ContributeContextAsync(request);
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(result, MemoryProviderJson), JsonSerializer.SerializeToElement(received, MemoryProviderJson)));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(AgentPublicSourceMigrationTests.PromptContextRequestWire.FromRequest(request), MemoryProviderJson),
                handler.Calls.Last(call => call.Method == "contribute").Request));
            Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(AgentPublicSourceMigrationTests.PromptContextRequestWire.FromRequest(request), MemoryProviderJson),
                JsonSerializer.SerializeToElement(AgentPublicSourceMigrationTests.PromptContextRequestWire.FromRequest(peer.Request!), MemoryProviderJson)));
            // Turn exposes interface ports; compare their borrowed originals in
            // the same assertion-side value projection as the concrete records.
            Assert.Same(peer.Request!.Session, AgentRoleBoundary.As<AgentSessionContextRecord>(peer.Request.Turn.Session));
            Assert.Same(peer.Request.Run, AgentRoleBoundary.As<AgentRunContextRecord>(peer.Request.Turn.Run));
        }
        var target = new AgentPublicSourceMigrationTests.PromptTargetProbe();
        var targetHandler = new Sunder.Generated.Rpc.ExecutionTarget.SunderAgentExecutionTargetRpcHandler(new RegressionTestExecutionProvider(target));
        state.Add("example.prompt.target", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget), targetHandler, targetHandler, Noop.Instance, "fixture.prompt.target", "2.0.0");
        var describes = target.Describes; // Registration inspects the target's facet metadata.
        var targetSnapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ExecutionTarget)).Providers);
        var reference = catalog.GetServiceReference(RegressionTestBindings.ExecutionTargets, ToProviderHandle(targetSnapshot));
        request = request with { ExecutionTargetReference = reference };
        await client.ContributeContextAsync(request);
        Assert.Equal(reference.ToHandle(), peer.Request!.ExecutionTargetReference!.ToHandle());
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(reference.ToHandle(), MemoryProviderJson),
            handler.Calls.Last(call => call.Method == "contribute").Request.GetProperty("executionTarget")));
        Assert.Equal(describes, target.Describes); // Forwarding the handle must not activate an execution client.
        await state.RetireAsync(targetHandler);
        state.Add("example.prompt.target", RegressionTestRpcDescriptors.Get(AgentRpcContractIds.ExecutionTarget), targetHandler, targetHandler, Noop.Instance, "fixture.prompt.target", "2.0.0");
        await client.ContributeContextAsync(request); Assert.Null(peer.Request!.ExecutionTargetReference);
        Assert.Equal(describes, target.Describes);
        request = request with { Profile = null, Workspace = null, ExecutionBinding = null, MemoryConsistencyBarrier = null, ExecutionTargetReference = null,
            AvailableTools = [], Turns = [], RecentLiveBufferTurns = [], ContextPlan = AgentPromptContextPlan.None(), TranscriptEpoch = 0 };
        await client.ContributeContextAsync(request);
        var minimal = handler.Calls.Last(call => call.Method == "contribute").Request;
        foreach (var field in new[] { "profile", "workspace", "executionBinding", "memoryConsistencyBarrier", "executionTarget" }) Assert.False(minimal.TryGetProperty(field, out _));
        Assert.False(peer.Request!.ContextPlan.ShouldContribute);
        var receipt = new AgentPromptContextReceipt(request.Session.SessionId, request.Run.RunId, request.TranscriptEpoch,
            [new("host.identity", new string('b', 64), "/workspace/AGENTS.md", new string('a', 64))]);
        await sink.AcknowledgePromptContextAsync(receipt);
        Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(receipt, MemoryProviderJson), JsonSerializer.SerializeToElement(peer.Receipt, MemoryProviderJson)));
        var acknowledgments = peer.Acknowledgments;
        Assert.Equal(SunderRpcErrorKind.Validation, (await Assert.ThrowsAsync<SunderRpcException>(() => sink.AcknowledgePromptContextAsync(
            receipt with { Blocks = [receipt.Blocks[0] with { ContentHash = "short" }] }).AsTask())).Error.Kind);
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
            observed.Transform = change; var calls = handler.Calls.Count;
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ContributeContextAsync(request).AsTask())).Error.Kind);
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => sink.AcknowledgePromptContextAsync(receipt).AsTask())).Error.Kind);
            Assert.Equal(calls, handler.Calls.Count);
        }
        observed.Transform = null; observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ContributeContextAsync(request).AsTask())).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;
        observed.LookupFailure = SunderRpcErrorKind.PermissionDenied;
        Assert.Equal(SunderRpcErrorKind.PermissionDenied, (await Assert.ThrowsAsync<SunderRpcException>(() => client.ContributeContextAsync(request).AsTask())).Error.Kind);
        observed.LookupFailure = null;
        foreach (var method in new[] { "contribute", "acknowledge" })
        {
            Task Call(CancellationToken token) => method == "contribute" ? client.ContributeContextAsync(request, token).AsTask() : sink.AcknowledgePromptContextAsync(receipt, token).AsTask();
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
        }
        handler.BlockMethod = "contribute"; handler.Gate = new();
        var retiring = client.ContributeContextAsync(request).AsTask(); await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await state.RetireAsync(handler).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retiring)).Error.Kind);
        handler = AddSurfaceProvider(state, "sunder.package.agent", registration);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => sink.AcknowledgePromptContextAsync(receipt).AsTask())).Error.Kind);
        var replacement = CreateAgentPromptClient(owner, observed, Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.PromptContextContributor)).Providers));
        await using var nextLifetime = (IAsyncDisposable)replacement;
        await replacement.ContributeContextAsync(request);
        handler.BlockMethod = "acknowledge"; handler.Gate = new();
        var stopping = ((IAgentPromptContextAcknowledgmentSink)replacement).AcknowledgePromptContextAsync(receipt).AsTask();
        await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopped = nextLifetime.DisposeAsync().AsTask(); Assert.Same(stopped, nextLifetime.DisposeAsync().AsTask());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stopping); await stopped.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => replacement.ContributorId).Error.Kind);
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
    }

    private static IAgentPromptContextContributor CreateAgentPromptClient(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot snapshot)
        => AgentRoleBoundary.As<IAgentPromptContextContributor>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedPromptContextClient", true)!, rpc, snapshot)!);

    private sealed class AgentPromptPeer : IAgentPromptContextContributor, IAgentPromptContextAcknowledgmentSink
    {
        public string ContributorId { get; init; } = "fixture.prompt.context";
        public string DisplayName => "Prompt fixture";
        public string AcknowledgmentSinkId => "fixture.prompt.receipts";
        internal AgentPromptContextRequest? Request;
        internal AgentPromptContextReceipt? Receipt;
        internal int Acknowledgments;
        internal AgentPromptContextContribution? Result = new([AgentPublicSourceMigrationTests.RichPromptBlock()]);
        public ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Request = request; return ValueTask.FromResult(Result); }
        public ValueTask AcknowledgePromptContextAsync(AgentPromptContextReceipt receipt, CancellationToken cancellationToken = default)
        { cancellationToken.ThrowIfCancellationRequested(); Receipt = receipt; Acknowledgments++; return ValueTask.CompletedTask; }
    }
}
