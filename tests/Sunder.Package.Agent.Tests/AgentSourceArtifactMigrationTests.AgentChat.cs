using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Xunit;

namespace Sunder.Package.Agent.Tests;

#pragma warning disable MEAI001 // Preserve the existing chat continuation-token wire contract.

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static Assembly LoadAgentChatOwner(AssemblyLoadContext load, string worker)
    {
        using var source = File.OpenRead(Path.Combine(SingleProjectRoleOutput(Path.Combine(Path.GetDirectoryName(worker)!, "Worker"), "worker"), "Sunder.Package.Agent.dll"));
        return load.LoadFromStream(source);
    }

    private static async Task<AgentChatSelection> CreateAgentChatProvider(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
    {
        var receiver = new GeneratedServiceTestScope(rpc);
        try
        {
            var contract = owner.GetType("Sunder.Generated.Packages.Sunder_Package_Agent_Services.IChatProviderService", true)!;
            var catalog = receiver.Providers(contract);
            var snapshot = (await Call(catalog, "ListAsync", CancellationToken.None))!;
            var entries = ((System.Collections.IEnumerable)snapshot.GetType().GetProperty("Providers")!.GetValue(snapshot)!).Cast<object>();
            var selected = Assert.Single(entries, entry =>
                Equals(entry.GetType().GetProperty("ActivationId")!.GetValue(entry), provider.ActivationId)
                && Equals(entry.GetType().GetProperty("ProviderId")!.GetValue(entry), provider.ProviderId));
            foreach (var name in new[] { "PackageId", "PackageVersion", "ProviderId", "ContractId", "ContractVersion",
                         "DescriptorSha256", "AbiSha256", "ActivationId", "ActivationEpoch", "SessionGeneration" })
                Assert.Equal(typeof(SunderRpcProviderSnapshot).GetProperty(name)!.GetValue(provider),
                    selected.GetType().GetProperty(name)!.GetValue(selected));
            return new(owner, receiver, selected, selected.GetType().GetProperty("Service")!.GetValue(selected)!);
        }
        catch { await receiver.DisposeAsync(); throw; }
    }

    private sealed record AgentChatSelection(Assembly Owner, GeneratedServiceTestScope Receiver, object Entry, object Service) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Receiver.DisposeAsync();
    }

    // IChatClient is shared across roles. Borrow the owner's actual client so
    // source probes observe its original failures, stream and disposal lifetime.
    private static Task<IChatClient> CreateAgentChatClientAsync(AgentChatSelection provider, AgentChatClientContext context)
    {
        var client = provider.Owner.GetType("Sunder.Package.Agent.Services.AgentModelChatClient", true)!;
        var constructor = Assert.Single(client.GetConstructors(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic));
        var stream = provider.Service.GetType().GetMethod("StreamChatAsync")!
            .CreateDelegate(constructor.GetParameters()[1].ParameterType, provider.Service);
        return Task.FromResult((IChatClient)AgentRoleBoundary.Create(client, context, stream));
    }

    internal static async Task VerifyAgentChatConsumer(Assembly owner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        using var providers = new RegressionTestSourceProviders();
        var peer = new AgentChatPeer();
        await using var providerReceiver = new GeneratedServiceTestScope(new RegressionTestRpcClient(state, "sunder.package.agent"));
        var registration = providers.Create(RegressionTestBindings.ChatProviders, peer, providerReceiver);
        state.AddGenerated("example.chat", peer, registration, Noop.Instance, "fixture.chat");
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var snapshot = Assert.Single((await rpc.DiscoverAsync(RegressionTestBindings.ChatProviders)).Providers);
        var observed = new FilesRpcObserver(rpc, "get-readiness");
        var provider = await CreateAgentChatProvider(owner, observed, snapshot);
        await using var lifetime = (IAsyncDisposable)provider;
        var description = AgentRoleBoundary.As<AgentProviderDescriptor>((await Call(provider.Service, "DescribeAsync", CancellationToken.None))!);
        Assert.Equal(peer.Descriptor.ProviderId, description.ProviderId);
        Assert.Equal(peer.Descriptor.DisplayName, description.DisplayName);
        Assert.Equal(peer.Descriptor.SupportedAuthModes, description.SupportedAuthModes);
        Assert.True(description.SupportsStreaming); Assert.True(description.SupportsInterruptibleRuns);
        Assert.Null(description.PackageId);
        var model = Assert.Single(AgentRoleBoundary.As<IReadOnlyList<AgentModelDescriptor>>((await Call(provider.Service, "ListModelsAsync", CancellationToken.None))!));
        Assert.Equal(peer.Model.ReleaseDate, model.ReleaseDate);
        Assert.Equal(AgentReasoningEffort.ExtraHigh, Assert.Single(model.Variants!).ReasoningEffort);
        Assert.Equal("fast", Assert.Single(model.SpeedOptions!).SpeedOptionId);
        Assert.True(Assert.Single(model.ModeOptions!).DisablesReasoning);
        Assert.Equal("chat/test", await Call(provider.Service, "ResolveUtilityModelAsync", CancellationToken.None));
        peer.UtilityModel = null;
        Assert.Null(await Call(provider.Service, "ResolveUtilityModelAsync", CancellationToken.None));
        Assert.Equal(AgentProviderReadinessStatus.Ready, AgentRoleBoundary.As<AgentProviderReadiness>((await Call(provider.Service, "GetReadinessAsync", CancellationToken.None))!).Status);
        Assert.Null(AgentRoleBoundary.As<AgentProviderRunCapabilities>((await Call(provider.Service, "GetCapabilitiesAsync", null, CancellationToken.None))!).ContextWindowTokens);
        Assert.Null(peer.RequestedModel);

        using var chat = await CreateAgentChatClientAsync(provider, new("chat", "chat/test"));
        var options = new ChatOptions
        {
            ModelId = "chat/test", Instructions = "Explicit instructions", Temperature = 0.5f, TopK = 0,
            MaxOutputTokens = 321, AllowMultipleToolCalls = false, Seed = 9_007_199_254_740_991,
            Reasoning = new() { Effort = ReasoningEffort.ExtraHigh },
            ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 0, 1, 255 }),
            AdditionalProperties = new() { ["nested"] = JsonSerializer.SerializeToElement(new { values = new object?[] { 1, false, null } }) },
            Tools = [AIFunctionFactory.CreateDeclaration("lookup", "Lookup", JsonSerializer.SerializeToElement(new { type = "object", properties = new { query = new { type = "string" } } }))],
        };
        ChatMessage[] messages = [new(ChatRole.User, [new TextContent("before 漢字"), new DataContent(new byte[] { 1, 2, 3 }, "image/png") { Name = "fixture.png" }, new TextContent("after")])];
        var scopesBefore = observed.Scopes.Count;
        var response = await chat.GetResponseAsync(messages, options);
        Assert.Contains("Generated chat", response.Text, StringComparison.Ordinal);
        Assert.Equal(scopesBefore + 1, observed.Scopes.Count);
        Assert.Equal(new byte[] { 1, 2, 3 }, Assert.IsType<DataContent>(Assert.Single(peer.Messages!).Contents[1]).Data.ToArray());
        Assert.Equal("fixture.png", Assert.IsType<DataContent>(peer.Messages![0].Contents[1]).Name);
        Assert.Equal(0.5f, peer.Options!.Temperature); Assert.Equal(0, peer.Options.TopK); Assert.False(peer.Options.AllowMultipleToolCalls);
        Assert.Equal(options.Seed, peer.Options.Seed); Assert.Equal(ReasoningEffort.ExtraHigh, peer.Options.Reasoning!.Effort);
        Assert.Equal(options.ContinuationToken.ToBytes().ToArray(), peer.Options.ContinuationToken!.ToBytes().ToArray());
        Assert.Equal("lookup", Assert.Single(peer.Options.Tools!).Name);
        Assert.Equal(JsonValueKind.Object, Assert.IsType<JsonElement>(peer.Options.AdditionalProperties!["nested"]).ValueKind);

        await using (var stream = chat.GetStreamingResponseAsync(messages, options).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            Assert.Equal(AgentChatPeer.CreatedAt, stream.Current.CreatedAt);
            Assert.Equal("protected", Assert.Single(stream.Current.Contents.OfType<TextReasoningContent>()).ProtectedData);
            Assert.True(Assert.Single(stream.Current.Contents.OfType<FunctionCallContent>()).InformationalOnly);
            Assert.Equal(0, Assert.Single(stream.Current.Contents.OfType<UsageContent>()).Details.InputTokenCount);
            Assert.Equal(new byte[] { 4, 5 }, stream.Current.ContinuationToken!.ToBytes().ToArray());
            Assert.False(await stream.MoveNextAsync());
        }
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); var calls = peer.Calls;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => chat.GetResponseAsync(messages, options, cancelled.Token));
            Assert.Equal(calls, peer.Calls);
        }
        observed.Transform = value => value with { ActivationEpoch = value.ActivationEpoch + 1 };
        var beforeStale = peer.Calls;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => chat.GetResponseAsync(messages))).Error.Kind);
        Assert.Equal(beforeStale, peer.Calls);
        observed.Transform = null;
        peer.BeforeUpdate = () => observed.Transform = value => value with { SessionGeneration = value.SessionGeneration + 1 };
        await using (var stream = chat.GetStreamingResponseAsync(messages).GetAsyncEnumerator())
            Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(async () => await stream.MoveNextAsync())).Error.Kind);
        peer.BeforeUpdate = null; observed.Transform = null;
        observed.InvalidateAfterInvoke = true;
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => Call(provider.Service, "GetReadinessAsync", CancellationToken.None))).Error.Kind);
        observed.InvalidateAfterInvoke = false; observed.Transform = null;

        using (var package = RegressionTestPackageScope.Create())
        {
            await using var host = new AgentPromptHost(owner, package.Context, rpc);
            var profiles = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!);
            var profile = host.Profile with
            {
                ChatProviderId = "chat", ChatModelId = "chat/test",
                ModelBindings = [new(host.Profile.ProfileId, AgentModelCapabilityKinds.Chat, "chat", "chat/test",
                    "{\"reasoningVariantId\":\"deep\",\"speedOptionId\":\"fast\"}", host.Started)],
            };
            SurfaceCall(profiles, "ImportProfile", profile);
            Assert.Equal("chat", Assert.Single((IReadOnlyList<AgentProviderDescriptor>)(await Call(profiles, "ListChatProviderDescriptorsAsync", CancellationToken.None))!).ProviderId);
            Assert.Equal("chat/test", Assert.Single((IReadOnlyList<AgentModelDescriptor>)(await Call(profiles, "ListChatModelsAsync", "CHAT", CancellationToken.None))!).ModelId);
            var resolver = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentRunProviderResolver", true)!);
            var selection = (await Call(resolver, "ResolveChatProviderAsync", profile, CancellationToken.None))!;
            var selected = selection.GetType().GetProperty("Provider", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(selection)!;
            Assert.Equal(provider.Service.GetType(), selected.GetType().GetProperty("Service")!.GetValue(selected)!.GetType());
            var metadata = (await Call(resolver, "ResolveRunMetadataAsync", selection, CancellationToken.None))!;
            var capabilities = AgentRoleBoundary.As<AgentProviderRunCapabilities>(metadata.GetType().GetProperty("RunCapabilities")!.GetValue(metadata)!);
            Assert.Equal(peer.Model.ContextWindow, capabilities.ContextWindowTokens); Assert.Equal(peer.Model.MaxOutputTokens, capabilities.MaxOutputTokens);
            Assert.Equal("deep", AgentRoleBoundary.As<AgentModelVariantDescriptor>(metadata.GetType().GetProperty("ModelVariant")!.GetValue(metadata)!).VariantId);
            var stack = (IPackageStackExporter)host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentProfileStackContributor", true)!);
            Assert.Contains((await stack.ExportAsync(new([new(profile.ProfileId)]))).PackageRequirements, requirement => requirement.PackageId == "example.chat");
        }

        peer.Gate = new();
        await using (var stream = chat.GetStreamingResponseAsync(messages).GetAsyncEnumerator())
        {
            Assert.True(await stream.MoveNextAsync());
            var pending = stream.MoveNextAsync().AsTask();
            await peer.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stopped = lifetime.DisposeAsync().AsTask();
            Assert.Same(stopped, lifetime.DisposeAsync().AsTask());
            var ended = await Assert.ThrowsAsync<SunderRpcException>(() => pending);
            Assert.Equal(SunderRpcErrorKind.Unavailable, ended.Error.Kind);
            Assert.Equal("rpc.binding.retired", ended.Error.Code);
            Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, ended.Error.DispatchState);
            Assert.Equal(SunderRpcRetrySafety.Unsafe, ended.Error.RetrySafety);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            await peer.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        var disposed = Assert.Throws<SunderRpcException>(() => AgentRoleBoundary.Call(provider.Entry, "get_Service"));
        Assert.Equal(SunderRpcErrorKind.Unavailable, disposed.Error.Kind);
        Assert.Equal("rpc.binding.lifetime-disposed", disposed.Error.Code);
        observed.AssertScopedOperations(); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
        Assert.Equal(peer.Calls, peer.Disposals);
    }

    private sealed class AgentChatPeer : IAgentChatProvider, IAgentUtilityModelProvider
    {
        internal static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-09-30T00:01:02.1234567Z");
        internal int Calls;
        internal int Disposals;
        internal FilesCallGate? Gate;
        internal Action? BeforeUpdate;
        internal IReadOnlyList<ChatMessage>? Messages;
        internal ChatOptions? Options;
        internal string? RequestedModel;
        internal string? UtilityModel = "chat/test";
        internal AgentModelDescriptor Model { get; } = new("chat/test", "Chat model", 8192, 1024, true,
            [new("deep", "Deep", ReasoningEffort: AgentReasoningEffort.ExtraHigh)], [new("fast", "Fast")], [new("quick", "Quick", DisablesReasoning: true)])
            { ReleaseDate = new(2026, 9, 30) };
        public AgentProviderDescriptor Descriptor { get; } = new("chat", "Generated chat", [AgentAuthMode.ApiKey, AgentAuthMode.CodexConnected], true, true);
        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([Model]);
        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(new AgentProviderReadiness("chat", AgentProviderReadinessStatus.Ready, "Ready"));
        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
        { RequestedModel = modelId; return ValueTask.FromResult(new AgentProviderRunCapabilities(true, true, false, "Chat")); }
        public ValueTask<string?> ResolveUtilityModelIdAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(UtilityModel);
        public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
        { Calls++; Assert.Equal("chat", context.ProviderId); Assert.Equal("chat/test", context.ModelId); return ValueTask.FromResult<IChatClient>(new Client(this)); }

        private sealed class Client(AgentChatPeer owner) : IChatClient
        {
            public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
            public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null,
                [EnumeratorCancellation] CancellationToken cancellationToken = default)
            {
                owner.Messages = messages.ToArray(); owner.Options = options;
                owner.BeforeUpdate?.Invoke();
                yield return new(ChatRole.Assistant, [new TextContent("Generated chat"), new TextReasoningContent("Reasoning") { ProtectedData = "protected" },
                    new FunctionCallContent("call.one", "lookup", new Dictionary<string, object?> { ["query"] = "value" }) { InformationalOnly = true },
                    new UsageContent(new() { InputTokenCount = 0, OutputTokenCount = 5, TotalTokenCount = 5 })])
                    { CreatedAt = CreatedAt, ContinuationToken = ResponseContinuationToken.FromBytes(new byte[] { 4, 5 }) };
                if (owner.Gate is { } gate)
                {
                    gate.Started.TrySetResult();
                    try { await gate.Release.Task.WaitAsync(cancellationToken); }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { gate.Cancelled.TrySetResult(); throw; }
                }
            }
            public object? GetService(Type serviceType, object? serviceKey = null) => null;
            public void Dispose() => owner.Disposals++;
        }
    }
}
