using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyPromptContextAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        var descriptor = descriptors[AgentRpcContractIds.PromptContextContributor];
        var probe = new PromptSinkProbe();
        await using var fixture = await CreatePromptFixture(assembly, descriptor, probe);
        var request = CreatePromptRequest();
        var expectedDescription = RunJson(new { probe.ContributorId, probe.DisplayName, probe.AcknowledgmentSinkId });
        var description = await InvokeGeneratedClient(fixture.Client, "DescribeAsync", EmptyRequest());
        Assert.True(JsonElement.DeepEquals(expectedDescription, description));
        Assert.Equal(probe.AcknowledgmentSinkId, description.GetProperty("acknowledgmentSinkId").GetString());
        foreach (var contribution in new AgentPromptContextContribution?[] { null, new([]), new([RichPromptBlock(), new("Empty", "", -1)]) })
        {
            probe.Result = contribution;
            var expected = RunJson(contribution);
            var arguments = RunJson(request);
            var actual = await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(request));
            Assert.True(JsonElement.DeepEquals(expected, actual));
            var received = RunJson(PromptContextRequestWire.FromRequest(probe.Request!));
            Assert.True(JsonElement.DeepEquals(arguments, received), $"Prompt domain arguments: {arguments} != {received}");
            Assert.Same(probe.Request!.Session, probe.Request.Turn.Session);
            Assert.Same(probe.Request.Run, probe.Request.Turn.Run);
        }
        foreach (var intent in new[] { "NoNe", "lookup" })
        {
            var wire = JsonNode.Parse(RunJson(request).GetRawText())!;
            wire["contextPlan"]!["intent"] = intent;
            wire["contextPlan"]!["shouldContribute"] = intent == "NoNe";
            await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(wire));
            Assert.Equal(intent != "NoNe", probe.Request!.ContextPlan.ShouldContribute);
        }
        var minimal = request with
        {
            Profile = null, Workspace = null, ExecutionBinding = null, MemoryConsistencyBarrier = null,
            AvailableTools = [], Turns = [], RecentLiveBufferTurns = [], ContextPlan = AgentPromptContextPlan.None(), TranscriptEpoch = 0,
        };
        await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(minimal));
        Assert.True(JsonElement.DeepEquals(RunJson(minimal), RunJson(PromptContextRequestWire.FromRequest(probe.Request!))));

        var receipt = new AgentPromptContextReceipt(request.Session.SessionId, request.Run.RunId, request.TranscriptEpoch,
            [new("host.identity", new string('b', 64), "/workspace/AGENTS.md", new string('a', 64))]);
        var expectedAck = EmptyRequest();
        var actualAck = await InvokeGeneratedClient(fixture.Client, "AcknowledgeAsync", RunJson(receipt));
        Assert.True(JsonElement.DeepEquals(expectedAck, actualAck));
        Assert.True(JsonElement.DeepEquals(RunJson(receipt), RunJson(probe.Receipt)));
        Assert.True(JsonElement.DeepEquals(expectedAck, await InvokeGeneratedClient(fixture.Client, "AcknowledgeAsync", RunJson(receipt))));
        Assert.Equal(2, probe.Acknowledgments);

        var invalid = JsonNode.Parse(RunJson(request).GetRawText())!;
        invalid["contextPlan"]!["maxChars"] = -1;
        var calls = probe.Contributions;
        Assert.Equal(SunderRpcErrorKind.Validation, (await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(invalid)))).Error.Kind);
        Assert.Equal(calls, probe.Contributions);
        var badReceipt = receipt with { Blocks = [receipt.Blocks[0] with { ContentHash = "short" }] };
        Assert.Equal(SunderRpcErrorKind.Validation, (await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, "AcknowledgeAsync", RunJson(badReceipt)))).Error.Kind);
        Assert.Equal(2, probe.Acknowledgments);

        foreach (var (method, payload) in new[] { ("ContributeAsync", RunJson(request)), ("AcknowledgeAsync", RunJson(receipt)) })
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var before = (probe.Contributions, probe.Acknowledgments);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeGeneratedClient(fixture.Client, method, payload, canceled.Token));
                Assert.Equal(before, (probe.Contributions, probe.Acknowledgments));
            }
            probe.Block = true;
            probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var canceled = new CancellationTokenSource())
            {
                var pending = InvokeGeneratedClient(fixture.Client, method, payload, canceled.Token);
                await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            probe.Block = false;
            probe.Failure = new IOException("private prompt backend detail");
            var failure = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(fixture.Client, method, payload));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
            Assert.DoesNotContain("private prompt", failure.Message, StringComparison.Ordinal);
            probe.Failure = null;
        }
        await VerifyPromptTargetsAsync(assembly, descriptors, fixture, probe, request);
        await VerifyWorkspacePromptAsync(assembly, descriptor);
        await VerifyPromptAuthorityAsync(assembly, descriptor);

        probe.Block = true;
        probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(request));
        await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await fixture.State.RetireAsync(fixture.Implementation).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => retired.WaitAsync(TimeSpan.FromSeconds(5)))).Error.Kind);
    }

    internal static PromptContextRequestWire CreatePromptRequest()
    {
        var runtime = new FullRunControlProbe();
        var context = runtime.Context;
        return new(new(context.Session.SessionId, context.Profile.ProfileId, context.Profile.DisplayName, context.Session.Title, AgentSessionState.Interrupted, "Session summary"),
            new(context.RunId, 7, AgentRunStatus.Running, true, context.RunStartedAtUtc), new("User message 漢字", "Turn summary"),
            [runtime.Turn], [runtime.Turn], new("lookup", "Query", "Reason", ["facts", "procedures"], 4, 2000),
            context.Profile, context.Workspace, context.ExecutionBinding,
            [new("tool.one", "Tool", "Description", false, true, "{opaque-schema", "kind", "source", "Source", ["alias"],
                AgentToolSelectionScope.Group, "group", "Group", "", "Instructions", new("capability.kind", "capability.one", "source.one"), AgentToolPriority.High)
            { ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe }], new("event.one", new string('c', 64)), 11, null);
    }

    internal static AgentPromptContextBlock RichPromptBlock()
        => new("Scoped context 漢字", "Reference content", 23, "claimed.source", AgentContextProvenance.Extension, AgentContextTrust.Untrusted)
        {
            Usage = AgentPromptContextUsage.ScopedInstruction, Authority = AgentPromptContextAuthority.StandingInstruction,
            HostIdentity = "claimed.host.identity", Scope = new("/workspace", "/workspace/src", "/workspace/AGENTS.md", new string('a', 64), new string('b', 64)),
        };

    private static async Task<PromptFixture> CreatePromptFixture(Assembly assembly, SunderRpcContractDescriptor descriptor,
        IAgentPromptContextContributor contributor, string packageId = "sunder.package.agent", bool includeCatalog = true)
    {
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var catalog = new AgentRpcCatalog(rpc.ForCaller(packageId));
        var type = assembly.GetType("Sunder.Package.Agent.Rpc.Public.PromptContextContributorService", true)!;
        var implementation = Activator.CreateInstance(type, contributor, includeCatalog ? catalog : null)!;
        var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(implementation, null));
        Assert.Equal("agent.workspace.documentation", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add(packageId, descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var providers = (await rpc.DiscoverAsync(descriptor.ContractId)).Providers;
        var endpoint = providers.Single(value => value.ProviderId == registration.ProviderId).Endpoint;
        var scope = await rpc.CreateCallScopeAsync();
        var client = Activator.CreateInstance(assembly.GetType("Sunder.Generated.Rpc.PromptContextContributor.PromptContextContributorClient", true)!, scope, endpoint)!;
        return new(state, rpc, catalog, scope, client, implementation, endpoint, packageId);
    }

    private sealed class PromptFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, AgentRpcCatalog catalog,
        ISunderRpcCallScope scope, object client, object implementation, SunderRpcEndpointReference source,
        string packageId) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public object Client { get; } = client;
        public object Implementation { get; } = implementation;
        public SunderRpcEndpointReference Source { get; } = source;
        public string PackageId { get; } = packageId;
        public async ValueTask DisposeAsync() { await scope.DisposeAsync(); catalog.Dispose(); State.Dispose(); }
    }

    private class PromptProbe : IAgentPromptContextContributor
    {
        public string ContributorId { get; set; } = "prompt.probe";
        public string DisplayName => "Prompt probe";
        public AgentPromptContextRequest? Request { get; private set; }
        public AgentPromptContextContribution? Result { get; set; } = new([RichPromptBlock()]);
        public int Contributions { get; private set; }
        public bool Block { get; set; }
        public Exception? Failure { get; set; }
        public TaskCompletionSource Started { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(AgentPromptContextRequest request, CancellationToken cancellationToken = default)
        {
            Contributions++; Request = request;
            await Wait(cancellationToken);
            return Result;
        }
        protected async ValueTask Wait(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (Block) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); }
            if (Failure is not null) throw Failure;
        }
    }

    private sealed class PromptSinkProbe : PromptProbe, IAgentPromptContextAcknowledgmentSink
    {
        public string AcknowledgmentSinkId => "prompt.sink";
        public AgentPromptContextReceipt? Receipt { get; private set; }
        public int Acknowledgments { get; private set; }
        public async ValueTask AcknowledgePromptContextAsync(AgentPromptContextReceipt receipt, CancellationToken cancellationToken = default)
        { Acknowledgments++; Receipt = receipt; await Wait(cancellationToken); }
    }
}
