using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyPromptContextAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        var descriptor = descriptors[RegressionTestBindings.PromptContextContributors];
        var probe = new PromptSinkProbe();
        await using var fixture = await CreatePromptFixture(assembly, descriptor, probe);
        var request = CreatePromptRequest();
        var expectedDescription = RunJson(new { probe.ContributorId, probe.DisplayName, probe.AcknowledgmentSinkId });
        var description = await fixture.Client.DescribeAsync();
        Assert.True(JsonElement.DeepEquals(expectedDescription, RunJson(description)));
        Assert.Equal(probe.AcknowledgmentSinkId, description.AcknowledgmentSinkId);
        foreach (var contribution in new AgentPromptContextContribution?[] { null, new([]), new([RichPromptBlock(), new("Empty", "", -1)]) })
        {
            probe.Result = contribution;
            var expected = RunJson(contribution);
            var arguments = RunJson(request);
            var actual = await fixture.Client.ContributeAsync(request.DomainRequestWithoutTarget());
            Assert.True(JsonElement.DeepEquals(expected, RunJson(actual)));
            var received = RunJson(PromptContextRequestWire.FromRequest(probe.Request!));
            Assert.True(JsonElement.DeepEquals(arguments, received), $"Prompt domain arguments: {arguments} != {received}");
            // Serialized values preserve every field, not CLR alias identity.
            // The complete wire snapshot above also covers the surrounding request.
            Assert.Equal(probe.Request!.Session, probe.Request.Turn.Session);
            Assert.Equal(probe.Request.Run, probe.Request.Turn.Run);
        }
        foreach (var intent in new[] { "NoNe", "lookup" })
        {
            await fixture.Client.ContributeAsync((request with { ContextPlan = request.ContextPlan with { Intent = intent } }).DomainRequestWithoutTarget());
            Assert.Equal(intent != "NoNe", probe.Request!.ContextPlan.ShouldContribute());
        }
        var minimal = request with
        {
            Profile = null, Workspace = null, ExecutionBinding = null, MemoryConsistencyBarrier = null,
            AvailableTools = [], Turns = [], RecentLiveBufferTurns = [], ContextPlan = AgentPromptContextPlan.None(), TranscriptEpoch = 0,
        };
        await fixture.Client.ContributeAsync(minimal.DomainRequestWithoutTarget());
        Assert.True(JsonElement.DeepEquals(RunJson(minimal), RunJson(PromptContextRequestWire.FromRequest(probe.Request!))));
        var validGeneratedRequest = fixture.State.LastInvocationRequest;

        var receipt = new AgentPromptContextReceipt(request.Session.SessionId, request.Run.RunId, request.TranscriptEpoch,
            [new("host.identity", new string('b', 64), "/workspace/AGENTS.md", new string('a', 64))]);
        await fixture.Client.AcknowledgeAsync(receipt);
        Assert.True(JsonElement.DeepEquals(RunJson(receipt), RunJson(probe.Receipt)));
        await fixture.Client.AcknowledgeAsync(receipt);
        Assert.Equal(2, probe.Acknowledgments);
        var validGeneratedReceipt = fixture.State.LastInvocationRequest;

        var invalid = request with { ContextPlan = request.ContextPlan with { MaxChars = -1 } };
        var calls = probe.Contributions;
        var peerCalls = fixture.State.InvocationAttemptCount;
        await Assert.ThrowsAsync<JsonException>(() => fixture.Client.ContributeAsync(invalid.DomainRequestWithoutTarget()).AsTask());
        Assert.Equal(peerCalls, fixture.State.InvocationAttemptCount);
        Assert.Equal(calls, probe.Contributions);
        // Mutate the actual generated request, preserving every other wire field.
        // This retains the distinct remote-validation assertion at the real broker.
        var invalidWire = JsonNode.Parse(validGeneratedRequest.GetRawText())!.AsObject();
        invalidWire["contextPlan"]!["maxChars"] = -1;
        var remote = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Rpc.InvokeAsync(fixture.Source,
            "prompt-context-contributor", "contribute", JsonSerializer.SerializeToElement(invalidWire)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, remote.Error.Kind);
        Assert.Equal(peerCalls + 1, fixture.State.InvocationAttemptCount);
        Assert.Equal(calls, probe.Contributions);
        var badReceipt = receipt with { Blocks = [receipt.Blocks[0] with { ContentHash = "short" }] };
        peerCalls = fixture.State.InvocationAttemptCount;
        await Assert.ThrowsAsync<JsonException>(() => fixture.Client.AcknowledgeAsync(badReceipt).AsTask());
        Assert.Equal(peerCalls, fixture.State.InvocationAttemptCount);
        Assert.Equal(2, probe.Acknowledgments);
        var invalidReceiptWire = JsonNode.Parse(validGeneratedReceipt.GetRawText())!.AsObject();
        invalidReceiptWire["blocks"]![0]!["contentHash"] = "short";
        var invalidReceipt = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Rpc.InvokeAsync(fixture.Source,
            "prompt-context-contributor", "acknowledge", JsonSerializer.SerializeToElement(invalidReceiptWire)).AsTask());
        Assert.Equal(SunderRpcErrorKind.Validation, invalidReceipt.Error.Kind);
        Assert.Equal(peerCalls + 1, fixture.State.InvocationAttemptCount);
        Assert.Equal(2, probe.Acknowledgments);

        foreach (var call in new Func<CancellationToken, Task>[]
        {
            token => fixture.Client.ContributeAsync(request.DomainRequestWithoutTarget(), token).AsTask(),
            token => fixture.Client.AcknowledgeAsync(receipt, token).AsTask(),
        })
        {
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                var before = (probe.Contributions, probe.Acknowledgments);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call(canceled.Token));
                Assert.Equal(before, (probe.Contributions, probe.Acknowledgments));
            }
            probe.Block = true;
            probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
            using (var canceled = new CancellationTokenSource())
            {
                var pending = call(canceled.Token);
                await probe.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                canceled.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            probe.Block = false;
            probe.Failure = new IOException("private prompt backend detail");
            var failure = await Assert.ThrowsAsync<SunderRpcException>(() => call(default));
            Assert.Equal(SunderRpcErrorKind.ProviderFaulted, failure.Error.Kind);
            Assert.DoesNotContain("private prompt", failure.Message, StringComparison.Ordinal);
            probe.Failure = null;
        }
        await VerifyPromptTargetsAsync(assembly, descriptors, fixture, probe, request);
        await VerifyWorkspacePromptAsync(assembly, descriptor);
        await VerifyPromptAuthorityAsync(assembly, descriptor);

        probe.Block = true;
        probe.Started = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = fixture.Client.ContributeAsync(request.DomainRequestWithoutTarget()).AsTask();
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
        IAgentPromptContextContributor contributor, string packageId = "younics.ai.agent", bool includeCatalog = true)
    {
        var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "younics.ai.agent");
        var receiver = new GeneratedServiceTestScope(rpc.ForCaller(packageId));
        var type = assembly.GetType("Sunder.Package.Agent.Rpc.Public.PromptContextContributorService", true)!;
        Assert.True(type.IsInterface);
        var implementation = contributor;
        using var providers = new RegressionTestSourceProviders();
        var registration = providers.Create(descriptor.ContractId, implementation, receiver);
        Assert.Equal(contributor is Sunder.Package.Agent.Services.WorkspaceDocumentationContextService
            ? "agent.workspace.documentation" : "fixture.regression.prompt.context", registration.ProviderId);
        Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
        Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
        state.Add(packageId, descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var provider = Assert.Single((await receiver.Providers<AgentServices.IPromptContextContributorService>().ListAsync()).Providers,
            value => value.ProviderId == registration.ProviderId);
        var endpoint = Assert.Single(state.Discover(descriptor.ContractId).Providers,
            value => value.ProviderId == registration.ProviderId).Endpoint;
        return new(state, rpc, receiver, provider.Service, implementation, endpoint, packageId);
    }

    private sealed class PromptFixture(RegressionTestRpcState state, RegressionTestRpcClient rpc, GeneratedServiceTestScope receiver,
        AgentServices.IPromptContextContributorService client, object implementation, SunderRpcEndpointReference source,
        string packageId) : IAsyncDisposable
    {
        public RegressionTestRpcState State { get; } = state;
        public RegressionTestRpcClient Rpc { get; } = rpc;
        public GeneratedServiceTestScope Receiver { get; } = receiver;
        public AgentServices.IPromptContextContributorService Client { get; } = client;
        public object Implementation { get; } = implementation;
        public SunderRpcEndpointReference Source { get; } = source;
        public string PackageId { get; } = packageId;
        public async ValueTask DisposeAsync() { await Receiver.DisposeAsync(); State.Dispose(); }
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
