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

    private static IAgentChatProvider CreateAgentChatProvider(Assembly owner, ISunderRpcClient rpc, SunderRpcProviderSnapshot provider)
        => AgentRoleBoundary.As<IAgentChatProvider>(Activator.CreateInstance(owner.GetType("Sunder.Package.Agent.Runtime.Rpc.GeneratedChatProviderClient", true)!, rpc, provider)!);

    // IChatClient is shared across roles. Borrow the owner's actual client so
    // source probes observe its original failures, stream and disposal lifetime.
    private static async Task<IChatClient> CreateAgentChatClientAsync(IAgentChatProvider provider, AgentChatClientContext context)
        => (IChatClient)(await AgentRoleBoundary.Await(AgentRoleBoundary.Call(provider,
            "CreateChatClientAsync", context, CancellationToken.None)!))!;

    internal static async Task VerifyAgentChatConsumer(Assembly owner)
    {
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        using var providers = new RegressionTestProviderRoles();
        var peer = new AgentChatPeer();
        var registration = providers.Create("Provider.Gemini", "Rpc.ChatProvider", peer);
        state.AddGenerated("example.chat", peer, registration, Noop.Instance, "fixture.chat");
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var snapshot = Assert.Single((await rpc.DiscoverAsync(AgentRpcContractIds.ChatProvider)).Providers);
        var observed = new FilesRpcObserver(rpc, "get-readiness");
        var provider = CreateAgentChatProvider(owner, observed, snapshot);
        await using var lifetime = (IAsyncDisposable)provider;
        Assert.Equal(peer.Descriptor.ProviderId, provider.Descriptor.ProviderId);
        Assert.Equal(peer.Descriptor.DisplayName, provider.Descriptor.DisplayName);
        Assert.Equal(peer.Descriptor.SupportedAuthModes, provider.Descriptor.SupportedAuthModes);
        Assert.True(provider.Descriptor.SupportsStreaming); Assert.True(provider.Descriptor.SupportsInterruptibleRuns);
        Assert.Null(provider.Descriptor.PackageId);
        var model = Assert.Single(await provider.GetAvailableModelsAsync());
        Assert.Equal(peer.Model.ReleaseDate, model.ReleaseDate);
        Assert.Equal(AgentReasoningEffort.ExtraHigh, Assert.Single(model.Variants!).ReasoningEffort);
        Assert.Equal("fast", Assert.Single(model.SpeedOptions!).SpeedOptionId);
        Assert.True(Assert.Single(model.ModeOptions!).DisablesReasoning);
        Assert.Equal("chat/test", await ((IAgentUtilityModelProvider)provider).ResolveUtilityModelIdAsync());
        peer.UtilityModel = null;
        Assert.Null(await ((IAgentUtilityModelProvider)provider).ResolveUtilityModelIdAsync());
        Assert.Equal(AgentProviderReadinessStatus.Ready, (await provider.GetReadinessAsync()).Status);
        Assert.Null((await provider.GetRunCapabilitiesAsync(null)).ContextWindowTokens);
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
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, (await Assert.ThrowsAsync<SunderRpcException>(() => provider.GetReadinessAsync().AsTask())).Error.Kind);
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
            Assert.Equal("chat", Assert.Single((IReadOnlyList<AgentProviderDescriptor>)SurfaceCall(profiles, "ListChatProviderDescriptors")!).ProviderId);
            Assert.Equal("chat/test", Assert.Single((IReadOnlyList<AgentModelDescriptor>)(await Call(profiles, "ListChatModelsAsync", "CHAT", CancellationToken.None))!).ModelId);
            var resolver = host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentRunProviderResolver", true)!);
            using var selection = (IDisposable)SurfaceCall(resolver, "ResolveChatProvider", profile)!;
            var selected = selection.GetType().GetProperty("Provider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(selection)!;
            Assert.Equal(AgentRoleBoundary.Unwrap(provider).GetType(), selected.GetType());
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
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            await stopped.WaitAsync(TimeSpan.FromSeconds(5));
            await peer.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, Assert.Throws<SunderRpcException>(() => provider.Descriptor).Error.Kind);
        Assert.Equal(0, observed.UnscopedLookups); Assert.All(observed.Scopes, scope => Assert.True(scope.Disposed));
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
