extern alias SubagentsWorker;

using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Models;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Rpc;
using SubagentChatService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Chat_Provider.IChatProviderService;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class CatalogFaultContainmentTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(6)]
    public async Task AgentCatalog_DescribesEachProviderOncePerRequestIncludingConcurrentRequests(int requestCount)
    {
        var chat = new CatalogChatProvider("z-chat");
        var otherChat = new CatalogChatProvider("a-chat");
        var embedding = new CatalogEmbeddingProvider("z-embedding");
        var otherEmbedding = new CatalogEmbeddingProvider("a-embedding");
        using var runtime = new AgentCatalogRuntime(catalog =>
        {
            catalog.AddProvider(RegressionTestBindings.ChatProviders, chat, "package.z.chat");
            catalog.AddProvider(RegressionTestBindings.ChatProviders, otherChat, "package.a.chat");
            catalog.AddProvider(RegressionTestBindings.EmbeddingProviders, embedding, "package.z.embedding");
            catalog.AddProvider(RegressionTestBindings.EmbeddingProviders, otherEmbedding, "package.a.embedding");
        });
        var chatDescriptions = chat.DescribeCount;
        var otherChatDescriptions = otherChat.DescribeCount;
        var embeddingDescriptions = embedding.DescribeCount;
        var otherEmbeddingDescriptions = otherEmbedding.DescribeCount;

        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var requests = Enumerable.Range(0, requestCount).Select(async index =>
        {
            await start.Task;
            return await runtime.Handler.HandleAsync(new AgentCatalogRequest(
                ChatProviderId: index % 2 == 0 ? "Z-CHAT" : "z-chat",
                EmbeddingProviderId: index % 2 == 0 ? "Z-EMBEDDING" : "z-embedding"));
        }).ToArray();
        start.SetResult();
        var projections = await Task.WhenAll(requests);

        Assert.Equal(requestCount, chat.DescribeCount - chatDescriptions);
        Assert.Equal(requestCount, otherChat.DescribeCount - otherChatDescriptions);
        Assert.Equal(requestCount, embedding.DescribeCount - embeddingDescriptions);
        Assert.Equal(requestCount, otherEmbedding.DescribeCount - otherEmbeddingDescriptions);
        Assert.Equal(requestCount, chat.ModelsCount);
        Assert.Equal(requestCount, chat.ReadinessCount);
        Assert.Equal(requestCount, embedding.ModelsCount);
        Assert.Equal(requestCount, embedding.ReadinessCount);
        Assert.Equal(0, otherChat.ModelsCount);
        Assert.Equal(0, otherChat.ReadinessCount);
        Assert.Equal(0, otherEmbedding.ModelsCount);
        Assert.Equal(0, otherEmbedding.ReadinessCount);
        Assert.All(projections, projection =>
        {
            Assert.Equal(new[] { "a-chat", "z-chat" }, projection.ChatProviders.Select(provider => provider.ProviderId));
            Assert.Equal(new[] { "a-embedding", "z-embedding" }, projection.EmbeddingProviders.Select(provider => provider.ProviderId));
            Assert.Equal("z-chat-model", Assert.Single(projection.ChatModels).ModelId);
            Assert.Equal("z-embedding-model", Assert.Single(projection.EmbeddingModels).ModelId);
            Assert.Equal("z-chat", projection.ChatReadiness?.ProviderId);
            Assert.Equal("z-embedding", projection.EmbeddingReadiness?.ProviderId);
        });
    }

    [Theory]
    [InlineData("chat-describe")]
    [InlineData("chat-models")]
    [InlineData("chat-readiness")]
    [InlineData("embedding-describe")]
    [InlineData("embedding-models")]
    [InlineData("embedding-readiness")]
    public async Task AgentCatalog_PropagatesPressureAndRediscoversFreshMetadataOnTheNextRequest(string operation)
    {
        var chat = new CatalogChatProvider("chat");
        var embedding = new CatalogEmbeddingProvider("embedding");
        using var runtime = new AgentCatalogRuntime(catalog =>
        {
            catalog.AddProvider(RegressionTestBindings.ChatProviders, chat, "package.chat");
            catalog.AddProvider(RegressionTestBindings.EmbeddingProviders, embedding, "package.embedding");
        });
        var request = new AgentCatalogRequest(ChatProviderId: "chat", EmbeddingProviderId: "embedding");
        if (operation == "chat-describe") chat.DescriptorFailure = SunderRpcErrorKind.ResourceExhausted;
        if (operation == "chat-models") chat.ModelsFailure = SunderRpcErrorKind.ResourceExhausted;
        if (operation == "chat-readiness") chat.ReadinessFailure = SunderRpcErrorKind.ResourceExhausted;
        if (operation == "embedding-describe") embedding.DescriptorFailure = SunderRpcErrorKind.ResourceExhausted;
        if (operation == "embedding-models") embedding.ModelsFailure = SunderRpcErrorKind.ResourceExhausted;
        if (operation == "embedding-readiness") embedding.ReadinessFailure = SunderRpcErrorKind.ResourceExhausted;

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => runtime.Handler.HandleAsync(request).AsTask());

        Assert.Equal(SunderRpcErrorKind.ResourceExhausted, failure.Error.Kind);
        chat.DescriptorFailure = chat.ModelsFailure = chat.ReadinessFailure = null;
        embedding.DescriptorFailure = embedding.ModelsFailure = embedding.ReadinessFailure = null;
        chat.DisplayName = "Recovered chat";
        embedding.DisplayName = "Recovered embedding";
        var projection = await runtime.Handler.HandleAsync(request);

        Assert.Equal("Recovered chat", Assert.Single(projection.ChatProviders).DisplayName);
        Assert.Equal("Recovered embedding", Assert.Single(projection.EmbeddingProviders).DisplayName);
        Assert.Single(projection.ChatModels);
        Assert.Single(projection.EmbeddingModels);
        Assert.Equal(AgentProviderReadinessStatus.Ready, projection.ChatReadiness?.Status);
        Assert.Equal(AgentProviderReadinessStatus.Ready, projection.EmbeddingReadiness?.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(" ")]
    [InlineData("missing")]
    public async Task AgentCatalog_DoesNotQueryModelsOrReadinessWithoutASelectedProvider(string? providerId)
    {
        var chat = new CatalogChatProvider("chat");
        var embedding = new CatalogEmbeddingProvider("embedding");
        using var runtime = new AgentCatalogRuntime(catalog =>
        {
            catalog.AddProvider(RegressionTestBindings.ChatProviders, chat, "package.chat");
            catalog.AddProvider(RegressionTestBindings.EmbeddingProviders, embedding, "package.embedding");
        });

        var projection = await runtime.Handler.HandleAsync(new AgentCatalogRequest(
            ChatProviderId: providerId, EmbeddingProviderId: providerId));

        Assert.Single(projection.ChatProviders);
        Assert.Single(projection.EmbeddingProviders);
        Assert.Empty(projection.ChatModels);
        Assert.Empty(projection.EmbeddingModels);
        Assert.Null(projection.ChatReadiness);
        Assert.Null(projection.EmbeddingReadiness);
        Assert.Equal(0, chat.ModelsCount);
        Assert.Equal(0, chat.ReadinessCount);
        Assert.Equal(0, embedding.ModelsCount);
        Assert.Equal(0, embedding.ReadinessCount);
    }

    [Fact]
    public async Task AgentCatalog_OmitsUnavailableContributorsAndKeepsHealthyProjection()
    {
        using var runtime = new AgentCatalogRuntime(catalog =>
        {
            catalog.AddProvider(
                RegressionTestBindings.ToolSources,
                new CatalogToolSource("unavailable-tools", SunderRpcErrorKind.Unavailable),
                "package.unavailable.tools");
            catalog.AddProvider(
                RegressionTestBindings.ToolSources,
                new CatalogToolSource("healthy-tools"),
                "package.healthy.tools");
            catalog.AddProvider(
                RegressionTestBindings.SelectableCapabilityProviders,
                new CatalogCapabilityProvider("unavailable-capabilities", SunderRpcErrorKind.StaleEndpoint),
                "package.unavailable.capabilities");
            catalog.AddProvider(
                RegressionTestBindings.SelectableCapabilityProviders,
                new CatalogCapabilityProvider("healthy-capabilities"),
                "package.healthy.capabilities");
            catalog.AddProvider(
                RegressionTestBindings.ChatProviders,
                new CatalogChatProvider("descriptor-failure", descriptorFailure: SunderRpcErrorKind.Unavailable),
                "package.descriptor.failure");
            catalog.AddProvider(
                RegressionTestBindings.ChatProviders,
                new CatalogChatProvider("healthy-chat"),
                "package.healthy.chat");
            catalog.AddProvider(
                RegressionTestBindings.ChatProviders,
                new CatalogChatProvider(
                    "unavailable-chat",
                    modelsFailure: SunderRpcErrorKind.StaleEndpoint,
                    readinessFailure: SunderRpcErrorKind.Unavailable),
                "package.unavailable.chat");
            catalog.AddBehaviorLoop(
                new CatalogBehaviorLoop("unavailable-loop", SunderRpcErrorKind.Unavailable),
                "package.unavailable.loop");
            catalog.AddBehaviorLoop(
                new CatalogBehaviorLoop("healthy-loop"),
                "package.healthy.loop");
            catalog.AddProvider(
                RegressionTestBindings.ProfileCapabilityConsumers,
                new CatalogCapabilityConsumer(SunderRpcErrorKind.StaleEndpoint),
                "package.unavailable.consumer");
            catalog.AddProvider(
                RegressionTestBindings.ProfileCapabilityConsumers,
                new CatalogCapabilityConsumer(),
                "package.healthy.consumer");
            catalog.AddProvider(
                RegressionTestBindings.ExecutionTargets,
                new CatalogExecutionTarget("healthy-target"),
                "package.healthy.target");
        });

        var projection = await runtime.Handler.HandleAsync(new AgentCatalogRequest(
            ChatProviderId: "unavailable-chat"));

        Assert.Equal(
            new[] { "healthy-chat", "unavailable-chat" },
            projection.ChatProviders.Select(static provider => provider.ProviderId).Order().ToArray());
        Assert.Equal("healthy-loop", Assert.Single(projection.BehaviorLoops).LoopId);
        Assert.Equal("healthy-target", Assert.Single(projection.ExecutionTargets).TargetId);
        Assert.Equal("healthy-tools-tool", Assert.Single(projection.LocalTools).Descriptor.ToolId);
        Assert.Equal("healthy-capabilities-capability", Assert.Single(projection.SelectableCapabilities).CapabilityId);
        Assert.True(projection.HasEmbeddingConsumers);
        Assert.Empty(projection.ChatModels);
        Assert.Equal(AgentProviderReadinessStatus.Failed, projection.ChatReadiness?.Status);
    }

    [Fact]
    public async Task SubagentQueries_ContainUnavailableContributorsAndProviderStatus()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(
            RegressionTestBindings.ToolSources,
            new CatalogToolSource("unavailable-tools", SunderRpcErrorKind.Unavailable),
            "package.unavailable.tools");
        catalog.AddProvider(
            RegressionTestBindings.ToolSources,
            new CatalogToolSource("healthy-tools"),
            "package.healthy.tools");
        catalog.AddProvider(
            RegressionTestBindings.SelectableCapabilityProviders,
            new CatalogCapabilityProvider("unavailable-capabilities", SunderRpcErrorKind.StaleEndpoint),
            "package.unavailable.capabilities");
        catalog.AddProvider(
            RegressionTestBindings.SelectableCapabilityProviders,
            new CatalogCapabilityProvider("healthy-capabilities"),
            "package.healthy.capabilities");
        catalog.AddProvider(
            RegressionTestBindings.ChatProviders,
            new CatalogChatProvider("descriptor-failure", descriptorFailure: SunderRpcErrorKind.Unavailable),
            "package.descriptor.failure");
        catalog.AddProvider(
            RegressionTestBindings.ChatProviders,
            new CatalogChatProvider("healthy-chat"),
            "package.healthy.chat");
        catalog.AddProvider(
            RegressionTestBindings.ChatProviders,
            new CatalogChatProvider(
                "unavailable-chat",
                modelsFailure: SunderRpcErrorKind.StaleEndpoint,
                readinessFailure: SunderRpcErrorKind.Unavailable),
            "package.unavailable.chat");
        var service = new SubagentService(new SubagentStore(scope.Context));
        service.CreateSubagent("Existing Subagent");
        await using var managementFixture = await catalog.CreateSubagentRuntimeManagementAsync(service);

        var management = await SubagentProjectionContent.ReadAsync<SubagentProjection>(
            managementFixture.Service.ManagementAsync(), CancellationToken.None);
        var providerModels = await SubagentProjectionContent.ReadAsync<SubagentProjection>(
            managementFixture.Service.ProviderModelsAsync("unavailable-chat"), CancellationToken.None);

        Assert.Single(management.Subagents ?? []);
        Assert.Equal(
            new[] { "healthy-chat", "unavailable-chat" },
            (management.Providers ?? []).Select(static provider => provider.ProviderId).Order().ToArray());
        Assert.Equal("healthy-tools-tool", Assert.Single(management.Tools ?? []).ToolId);
        Assert.Equal("healthy-capabilities-capability", Assert.Single(management.Capabilities ?? []).CapabilityId);
        var unavailable = Assert.Single(providerModels.Providers ?? []);
        Assert.Empty(unavailable.Models ?? []);
        Assert.Equal(AgentProviderReadinessStatus.Failed.ToString(), unavailable.Readiness?.Status.ToString());
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Validation)]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded)]
    [InlineData(SunderRpcErrorKind.ResourceExhausted)]
    public async Task AgentCatalog_DoesNotSuppressStrictRpcFailures(SunderRpcErrorKind errorKind)
    {
        using var runtime = new AgentCatalogRuntime(catalog => catalog.AddProvider(
            RegressionTestBindings.ChatProviders,
            new CatalogChatProvider("strict-chat", modelsFailure: errorKind),
            "package.strict.chat"));

        var exception = await Assert.ThrowsAsync<SunderRpcException>(() =>
            runtime.Profiles.ListChatModelsAsync("strict-chat"));

        Assert.Equal(errorKind, exception.Error.Kind);
        var catalogException = await Assert.ThrowsAsync<SunderRpcException>(() =>
            runtime.Handler.HandleAsync(new AgentCatalogRequest(ChatProviderId: "strict-chat")).AsTask());
        Assert.Equal(errorKind, catalogException.Error.Kind);
    }

    [Theory]
    [InlineData(SunderRpcErrorKind.PermissionDenied)]
    [InlineData(SunderRpcErrorKind.Validation)]
    [InlineData(SunderRpcErrorKind.DeadlineExceeded)]
    public async Task SubagentProviderModels_DoesNotSuppressStrictRpcFailures(SunderRpcErrorKind errorKind)
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(
            RegressionTestBindings.ChatProviders,
            new CatalogChatProvider("strict-chat", modelsFailure: errorKind),
            "package.strict.chat");
        await using var managementFixture = await catalog.CreateSubagentRuntimeManagementAsync(
            new SubagentService(new SubagentStore(scope.Context)));

        var exception = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            await SubagentProjectionContent.ReadAsync<SubagentProjection>(
                managementFixture.Service.ProviderModelsAsync("strict-chat"), CancellationToken.None));

        Assert.Equal(errorKind, exception.Error.Kind);
    }

    [Fact]
    public async Task AgentInvocation_RemoteCancelledByCallerBecomesCallerCancellation()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var provider = new BlockingCancelledChatProvider("caller-cancelled");
        catalog.AddProvider(RegressionTestBindings.ChatProviders, provider, "package.caller.cancelled");
        var reference = await catalog.GetRequiredProviderAsync(catalog.ChatProviders);
        using var cancellation = new CancellationTokenSource();
        var invocation = reference.Service.ListModelsAsync(cancellation.Token).AsTask();
        await provider.Started.Task;

        cancellation.Cancel();
        await provider.CancellationObserved.Task;
        provider.ReleaseCancellation.TrySetResult();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => invocation);
        Assert.IsNotType<AgentPackageUnavailableException>(exception);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(
            SunderRpcErrorKind.Cancelled,
            Assert.IsType<SunderRpcException>(exception.InnerException).Error.Kind);
    }

    [Fact]
    public async Task AgentInvocation_RemoteCancelledByRetirementBecomesPackageUnavailable()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var failure = new SunderRpcException(new(SunderRpcErrorKind.Cancelled,
            "test.catalog.rpc-failure", "Retired in-flight provider call.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var provider = new BlockingCancelledChatProvider("retired", failure);
        catalog.AddProvider(RegressionTestBindings.ChatProviders, provider, "package.retired");
        var reference = await catalog.GetRequiredProviderAsync(catalog.ChatProviders);
        var invocation = reference.Service.ListModelsAsync().AsTask();
        await provider.Started.Task;

        var bindingRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var retirementObserved = reference.RetirementToken.Register(() => bindingRetired.TrySetResult());
        var retirement = catalog.RetireProviderAsync(RegressionTestBindings.ChatProviders, provider);
        await provider.CancellationObserved.Task;
        // Host-side cancellation and the generated binding observer are separate
        // signals. Exercise the classified retirement after that binding sees it.
        try { await bindingRetired.Task.WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { provider.ReleaseCancellation.TrySetResult(); }

        // This is a direct generated SDK call. Agent-domain unavailable mapping
        // is covered through the operational tool-catalog business consumer.
        var exception = await Assert.ThrowsAsync<SunderRpcException>(() => invocation);
        await retirement;
        Assert.Equal(SunderRpcErrorKind.Unavailable, exception.Error.Kind);
        Assert.Equal("rpc.binding.retired", exception.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, exception.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, exception.Error.RetrySafety);
        var stale = await Assert.ThrowsAsync<SunderRpcException>(() => reference.EnsureCurrentAsync().AsTask());
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint, stale.Error.Kind);
        Assert.True(reference.RetirementToken.IsCancellationRequested);
        var replacement = new BlockingCancelledChatProvider("retired");
        catalog.AddProvider(RegressionTestBindings.ChatProviders, replacement, "package.retired");
        var current = await catalog.GetRequiredProviderAsync(catalog.ChatProviders);
        Assert.NotEqual(reference.ActivationId, current.ActivationId);
        Assert.Equal(SunderRpcErrorKind.StaleEndpoint,
            Assert.Throws<SunderRpcException>(() => reference.Service).Error.Kind);
        Assert.False(replacement.Started.Task.IsCompleted);
    }

    [Fact]
    public async Task SubagentProviderModels_RemoteCancelledByCallerBecomesCallerCancellation()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var failure = new SunderRpcException(new(SunderRpcErrorKind.Cancelled,
            "test.catalog.rpc-failure", "Caller cancelled the in-flight provider call.",
            SunderRpcDispatchState.PossiblyDispatched, SunderRpcRetrySafety.Unsafe));
        var provider = new BlockingCancelledChatProvider("caller-cancelled", failure);
        catalog.AddProvider(RegressionTestBindings.ChatProviders, provider, "package.caller.cancelled");
        await using var managementFixture = await catalog.CreateSubagentRuntimeManagementAsync(
            new SubagentService(new SubagentStore(scope.Context)));
        using var cancellation = new CancellationTokenSource();
        var invocation = SubagentProjectionContent.ReadAsync<SubagentProjection>(
            managementFixture.Service.ProviderModelsAsync(provider.ProviderId, cancellation.Token), cancellation.Token).AsTask();
        await provider.Started.Task;

        cancellation.Cancel();
        await provider.CancellationObserved.Task;
        provider.ReleaseCancellation.TrySetResult();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => invocation);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Equal(
            SunderRpcErrorKind.Cancelled,
            Assert.IsType<SunderRpcException>(exception.InnerException).Error.Kind);
        var cause = Assert.IsType<SunderRpcException>(exception.InnerException);
        Assert.Equal(failure.Error.Code, cause.Error.Code);
        Assert.Equal(SunderRpcDispatchState.PossiblyDispatched, cause.Error.DispatchState);
        Assert.Equal(SunderRpcRetrySafety.Unsafe, cause.Error.RetrySafety);
    }

    [Fact]
    public async Task SubagentProviderModels_RemoteCancelledByRetirementReturnsFailedReadiness()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var provider = new BlockingCancelledChatProvider("retired");
        catalog.AddProvider(RegressionTestBindings.ChatProviders, provider, "package.retired");
        var reference = await catalog.GetRequiredProviderAsync(catalog.Subagents().Providers<SubagentChatService>());
        var referenceRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var referenceRetirement = reference.RetirementToken.Register(() => referenceRetired.TrySetResult());
        await using var managementFixture = await catalog.CreateSubagentRuntimeManagementAsync(
            new SubagentService(new SubagentStore(scope.Context)));
        var invocation = SubagentProjectionContent.ReadAsync<SubagentProjection>(
            managementFixture.Service.ProviderModelsAsync(provider.ProviderId), CancellationToken.None).AsTask();
        try
        {
            await provider.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var retirement = catalog.RetireProviderAsync(RegressionTestBindings.ChatProviders, provider);
            // Wait for the actual Subagents receiver used by management, not the
            // independent Agent receiver. Host-side cancellation alone is not
            // proof that this exact generated binding has observed withdrawal.
            await Task.WhenAll(provider.CancellationObserved.Task, referenceRetired.Task).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(reference.RetirementToken.IsCancellationRequested);
            provider.ReleaseCancellation.TrySetResult();

            var projection = await invocation.WaitAsync(TimeSpan.FromSeconds(5));
            await retirement.WaitAsync(TimeSpan.FromSeconds(5));
            var unavailable = Assert.Single(projection.Providers ?? []);
            Assert.Empty(unavailable.Models ?? []);
            Assert.Equal(AgentProviderReadinessStatus.Failed.ToString(), unavailable.Readiness?.Status.ToString());
        }
        finally { provider.ReleaseCancellation.TrySetResult(); }
    }

    [Fact]
    public async Task OperationalToolCatalog_DoesNotContainInternalSourceFailure()
    {
        using var runtime = new AgentCatalogRuntime(catalog => catalog.AddProvider(
            RegressionTestBindings.ToolSources,
            new CatalogToolSource("broken-tools", internalFailure: true),
            "package.broken.tools"));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runtime.Tools.ListReadyOwnedRuntimeToolsAsync());
    }

    [Fact]
    public async Task OperationalToolCatalog_DoesNotOmitUnavailableSource()
    {
        using var runtime = new AgentCatalogRuntime(catalog => catalog.AddProvider(
            RegressionTestBindings.ToolSources,
            new CatalogToolSource("unavailable-tools", SunderRpcErrorKind.Unavailable),
            "package.unavailable.tools"));

        var exception = await Assert.ThrowsAsync<AgentPackageUnavailableException>(() =>
            runtime.Tools.ListReadyOwnedRuntimeToolsAsync());

        Assert.Equal("package.unavailable.tools", exception.PackageId);
    }

    [Fact]
    public async Task SubagentManagement_DoesNotContainStoreFailure()
    {
        using var scope = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var storePath = scope.Context.Storage.RoleLocalWorkspace.GetLocalPath(
            "subagents/subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        File.WriteAllText(storePath, "{broken");
        await using var managementFixture = await catalog.CreateSubagentRuntimeManagementAsync(
            new SubagentService(new SubagentStore(scope.Context)));

        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await SubagentProjectionContent.ReadAsync<SubagentProjection>(
                managementFixture.Service.ManagementAsync(), CancellationToken.None));
    }

    private static SunderRpcException RpcFailure(SunderRpcErrorKind kind)
        => new(new SunderRpcError(
            kind,
            "test.catalog.rpc-failure",
            $"Injected {kind} catalog failure."));

    private sealed class AgentCatalogRuntime : IDisposable
    {
        private readonly RegressionTestPackageScope _scope;
        private readonly RegressionTestExtensionCatalog _catalog;
        private readonly AgentRuntimeChangeHub _changes;

        public AgentCatalogRuntime(Action<RegressionTestExtensionCatalog> configure)
        {
            _scope = RegressionTestPackageScope.Create();
            _catalog = new RegressionTestExtensionCatalog();
            configure(_catalog);
            var store = new AgentLocalStore(_scope.Context);
            var sessions = new AgentSessionService(store, _catalog.SessionCleaners);
            var workspaces = new AgentWorkspaceService(store, _catalog.SessionCleaners, sessions);
            var targets = new AgentExecutionTargetService(_catalog.ExecutionTargets);
            Tools = new AgentToolService(sessions, workspaces, targets, _catalog.ToolSources, _catalog.ObservedToolExecutions);
            Profiles = _catalog.CreateProfileService(store, Tools);
            _changes = new AgentRuntimeChangeHub(Profiles, workspaces, sessions);
            Handler = new AgentCatalogHandler(Profiles, targets, _changes);
        }

        public AgentToolService Tools { get; }
        public AgentProfileService Profiles { get; }
        public AgentCatalogHandler Handler { get; }

        public void Dispose()
        {
            _changes.Dispose();
            Profiles.Dispose();
            _catalog.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class CatalogChatProvider(
        string providerId,
        SunderRpcErrorKind? descriptorFailure = null,
        SunderRpcErrorKind? modelsFailure = null,
        SunderRpcErrorKind? readinessFailure = null) : IAgentChatProvider
    {
        private int _describeCount;
        private int _modelsCount;
        private int _readinessCount;
        public int DescribeCount => Volatile.Read(ref _describeCount);
        public int ModelsCount => Volatile.Read(ref _modelsCount);
        public int ReadinessCount => Volatile.Read(ref _readinessCount);
        public string? DisplayName { get; set; }
        public SunderRpcErrorKind? DescriptorFailure { get; set; } = descriptorFailure;
        public SunderRpcErrorKind? ModelsFailure { get; set; } = modelsFailure;
        public SunderRpcErrorKind? ReadinessFailure { get; set; } = readinessFailure;
        private readonly AgentProviderDescriptor _descriptor = new(
            providerId,
            providerId,
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public AgentProviderDescriptor Descriptor
        {
            get
            {
                Interlocked.Increment(ref _describeCount);
                return DescriptorFailure is { } failure ? throw RpcFailure(failure) : _descriptor with { DisplayName = DisplayName ?? _descriptor.DisplayName };
            }
        }

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _modelsCount);
            return ModelsFailure is { } failure
                ? ValueTask.FromException<IReadOnlyList<AgentModelDescriptor>>(RpcFailure(failure))
                : ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([
                    new($"{providerId}-model", $"{providerId} model", 8_192, 1_024),
                ]);
        }

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readinessCount);
            return ReadinessFailure is { } failure
                ? ValueTask.FromException<AgentProviderReadiness>(RpcFailure(failure))
                : ValueTask.FromResult(new AgentProviderReadiness(
                    providerId,
                    AgentProviderReadinessStatus.Ready,
                    "Ready."));
        }

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CatalogEmbeddingProvider(string providerId) : IAgentEmbeddingProvider
    {
        private int _describeCount;
        private int _modelsCount;
        private int _readinessCount;
        public int DescribeCount => Volatile.Read(ref _describeCount);
        public int ModelsCount => Volatile.Read(ref _modelsCount);
        public int ReadinessCount => Volatile.Read(ref _readinessCount);
        public string? DisplayName { get; set; }
        public SunderRpcErrorKind? DescriptorFailure { get; set; }
        public SunderRpcErrorKind? ModelsFailure { get; set; }
        public SunderRpcErrorKind? ReadinessFailure { get; set; }

        public AgentEmbeddingProviderDescriptor Descriptor
        {
            get
            {
                Interlocked.Increment(ref _describeCount);
                return DescriptorFailure is { } failure ? throw RpcFailure(failure) : new(providerId, DisplayName ?? providerId, []);
            }
        }

        public ValueTask<IReadOnlyList<AgentEmbeddingModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _modelsCount);
            return ModelsFailure is { } failure
                ? ValueTask.FromException<IReadOnlyList<AgentEmbeddingModelDescriptor>>(RpcFailure(failure))
                : ValueTask.FromResult<IReadOnlyList<AgentEmbeddingModelDescriptor>>([new($"{providerId}-model", "Embedding model", 128)]);
        }

        public ValueTask<AgentEmbeddingProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _readinessCount);
            return ReadinessFailure is { } failure
                ? ValueTask.FromException<AgentEmbeddingProviderReadiness>(RpcFailure(failure))
                : ValueTask.FromResult(new AgentEmbeddingProviderReadiness(providerId, AgentProviderReadinessStatus.Ready, "Ready."));
        }

        public ValueTask<AgentEmbeddingGenerationResult?> GenerateEmbeddingAsync(
            string modelId, string text, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IReadOnlyList<AgentEmbeddingGenerationResult?>> GenerateEmbeddingsAsync(
            string modelId, IReadOnlyList<string> texts, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class BlockingCancelledChatProvider(string providerId, SunderRpcException? failure = null) : IAgentChatProvider
    {
        public string ProviderId => providerId;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseCancellation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public AgentProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public async ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
        {
            Started.TrySetResult();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return [];
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult();
                await ReleaseCancellation.Task;
                throw failure ?? RpcFailure(SunderRpcErrorKind.Cancelled);
            }
        }

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(
                providerId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CatalogToolSource(
        string sourceId,
        SunderRpcErrorKind? failureKind = null,
        bool internalFailure = false) : IAgentToolSource
    {
        private readonly AgentToolDescriptor _tool = new(
            $"{sourceId}-tool",
            $"{sourceId} tool",
            "Catalog test tool.",
            SourceKind: "test",
            SourceId: sourceId,
            SourceDisplayName: sourceId);

        public string SourceId => sourceId;
        public string DisplayName => sourceId;
        public string SourceKind => "test";

        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
        {
            if (internalFailure) throw new InvalidOperationException("Injected internal tool catalog failure.");
            return failureKind is { } failure
                ? ValueTask.FromException<IReadOnlyList<AgentToolDescriptor>>(RpcFailure(failure))
                : ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([_tool]);
        }

        public ValueTask<AgentToolReadiness?> GetReadinessAsync(
            string toolId,
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentToolReadiness?>(new AgentToolReadiness(
                toolId,
                AgentToolReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentToolResult(request.ToolId, "Executed."));
    }

    private sealed class CatalogCapabilityProvider(
        string providerId,
        SunderRpcErrorKind? failureKind = null) : IAgentProfileSelectableCapabilityProvider
    {
        public string ProviderId => providerId;
        public string DisplayName => providerId;

        public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
            AgentProfileSelectableCapabilityRequest request,
            CancellationToken cancellationToken = default)
            => failureKind is { } failure
                ? ValueTask.FromException<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>(RpcFailure(failure))
                : ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([
                    new(
                        "test",
                        $"{providerId}-capability",
                        providerId,
                        $"{providerId} capability",
                        "Catalog test capability."),
                ]);
    }

    private sealed class CatalogBehaviorLoop(
        string loopId,
        SunderRpcErrorKind? descriptorFailure = null) : IAgentBehaviorLoop
    {
        private readonly AgentBehaviorLoopDescriptor _descriptor = new(
            loopId,
            loopId,
            "Catalog test behavior loop.");

        public AgentBehaviorLoopDescriptor Descriptor
            => descriptorFailure is { } failure ? throw RpcFailure(failure) : _descriptor;

        public ValueTask<AgentBehaviorLoopResult> RunAsync(
            AgentBehaviorLoopContext context,
            IAgentBehaviorLoopRuntime runtime,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class CatalogCapabilityConsumer(SunderRpcErrorKind? failureKind = null)
        : IAgentProfileCapabilityConsumer
    {
        public string ConsumerId => "catalog-consumer";
        public string DisplayName => "Catalog Consumer";

        public IReadOnlyList<AgentProfileCapabilityConsumerDescriptor> ListConsumedCapabilities()
            => failureKind is { } failure
                ? throw RpcFailure(failure)
                : [new(
                    AgentModelCapabilityKinds.Embedding,
                    "Embeddings",
                    "Consumes embeddings for catalog tests.")];
    }

    private sealed class CatalogExecutionTarget(string targetId) : IAgentExecutionTarget
    {
        public AgentExecutionTargetDescriptor Descriptor { get; } = new(
            "test",
            targetId,
            targetId,
            "Catalog test execution target.",
            SupportsShell: false,
            SupportsFiles: false);

        public ValueTask<AgentExecutionTargetReadiness> GetReadinessAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentExecutionShellDescriptor> GetShellAsync(
            AgentExecutionTargetContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentResolvedResource> ResolveFileResourceAsync(
            AgentExecutionTargetContext context,
            string path,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentShellCommandResult> ExecuteShellAsync(
            AgentExecutionTargetContext context,
            AgentShellCommandRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileReadResult> ReadFileAsync(
            AgentExecutionTargetContext context,
            AgentFileReadRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> WriteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileWriteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentFileMutationResult> DeleteFileAsync(
            AgentExecutionTargetContext context,
            AgentFileDeleteRequest request,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}
