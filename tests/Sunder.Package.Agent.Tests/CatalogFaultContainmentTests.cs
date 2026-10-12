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
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class CatalogFaultContainmentTests
{
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
    public async Task AgentCatalog_DoesNotSuppressStrictRpcFailures(SunderRpcErrorKind errorKind)
    {
        using var runtime = new AgentCatalogRuntime(catalog => catalog.AddProvider(
            RegressionTestBindings.ChatProviders,
            new CatalogChatProvider("strict-chat", modelsFailure: errorKind),
            "package.strict.chat"));

        var exception = await Assert.ThrowsAsync<SunderRpcException>(() =>
            runtime.Profiles.ListChatModelsAsync("strict-chat"));

        Assert.Equal(errorKind, exception.Error.Kind);
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
        var reference = await catalog.GetRequiredProviderAsync(catalog.ChatProviders);
        await using var managementFixture = await catalog.CreateSubagentRuntimeManagementAsync(
            new SubagentService(new SubagentStore(scope.Context)));
        var invocation = SubagentProjectionContent.ReadAsync<SubagentProjection>(
            managementFixture.Service.ProviderModelsAsync(provider.ProviderId), CancellationToken.None).AsTask();
        await provider.Started.Task;

        var retirement = catalog.RetireProviderAsync(RegressionTestBindings.ChatProviders, provider);
        await provider.CancellationObserved.Task;
        Assert.True(reference.RetirementToken.IsCancellationRequested);
        provider.ReleaseCancellation.TrySetResult();

        var projection = await invocation;
        await retirement;
        var unavailable = Assert.Single(projection.Providers ?? []);
        Assert.Empty(unavailable.Models ?? []);
        Assert.Equal(AgentProviderReadinessStatus.Failed.ToString(), unavailable.Readiness?.Status.ToString());
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
            var workspaces = new AgentWorkspaceService(store, _catalog.SessionCleaners, sessions, _catalog.WorkspacePathMigrators);
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
        private readonly AgentProviderDescriptor _descriptor = new(
            providerId,
            providerId,
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public AgentProviderDescriptor Descriptor
            => descriptorFailure is { } failure ? throw RpcFailure(failure) : _descriptor;

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => modelsFailure is { } failure
                ? ValueTask.FromException<IReadOnlyList<AgentModelDescriptor>>(RpcFailure(failure))
                : ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([
                    new($"{providerId}-model", $"{providerId} model", 8_192, 1_024),
                ]);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => readinessFailure is { } failure
                ? ValueTask.FromException<AgentProviderReadiness>(RpcFailure(failure))
                : ValueTask.FromResult(new AgentProviderReadiness(
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
