extern alias SkillsWorker;
extern alias SubagentsWorker;
extern alias BuilderWorker;
extern alias McpWorker;
extern alias MemoryWorker;
extern alias AgentApp;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Sunder.Package.Agent.Contracts.Contracts;
using AgentApp::Sunder.Package.Agent.Contracts.Models;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using IAgentOwnerRuntimeProvider = AgentApp::Sunder.Generated.Packages.Sunder_Package_Agent_Services.IAgentRuntimeService;
using Sunder.Package.Agent.Protocol;
using AgentApp::Sunder.Package.Agent.Runtime;
using AgentOwnerRuntimeProvider = Sunder.Package.Agent.Runtime.AgentOwnerRuntimeProvider;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using System.Reflection;
using System.Text.Json;
using Xunit;
using BuilderRuntimeService = BuilderWorker::Sunder.Package.Agent.Builder.BuilderRuntimeService;
using BuilderServices = BuilderWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using MemoryServices = MemoryWorker::Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using SkillOwnerJson = SkillsWorker::Sunder.Package.Agent.Skills.Runtime.OwnerJson;
using SkillOwnerOperationException = SkillsWorker::Sunder.Package.Agent.Skills.Runtime.SkillOwnerOperationException;
using SubagentOwnerJson = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime.SubagentOwnerJson;

namespace Sunder.Package.Agent.Tests;

public sealed class OwnerRpcFaultMappingTests
{
    private const string UnexpectedMessage = "unexpected-provider-secret-canary";

    [Fact]
    public async Task OwnerAdapters_MapOnlyExplicitDomainExceptions()
    {
        var unexpected = new InvalidOperationException(UnexpectedMessage);

        Assert.False(SkillOwnerJson.ShouldMap(unexpected));
        Assert.False(SubagentOwnerJson.ShouldMap(unexpected));
        Assert.True(SkillOwnerJson.ShouldMap(new SkillOwnerOperationException("expected")));
        Assert.True(SubagentOwnerJson.ShouldMap(new SubagentOwnerOperationException("expected")));

        await using var builderResolvers = new FakeServiceCatalog<BuilderServices.IWorkspaceExecutionResolverService>(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.WorkspaceExecutionResolver));
        var builderResolver = ThrowingBusinessService.Create<BuilderServices.IWorkspaceExecutionResolverService>(unexpected);
        builderResolvers.Add(builderResolver);
        var builder = new BuilderRuntimeService(builderResolvers);
        var builderRegistration = builder.CreateSunderRpcProviderRegistration();

        var builderAuthored = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeAsync(
            "sunder.package.agent.builder",
            builder,
            builderRegistration,
            "resolve-workspace",
            "\"\""));
        Assert.Equal(SunderRpcErrorKind.Domain, builderAuthored.Error.Kind);
        Assert.Equal("agent.builder.invalid-request", builderAuthored.Error.Code);
        Assert.DoesNotContain(UnexpectedMessage, builderAuthored.Error.Message, StringComparison.Ordinal);

        Assert.Same(unexpected, await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            "sunder.package.agent.builder",
            builder,
            builderRegistration,
            "list-workspaces",
            "{}")));

        using var package = RegressionTestPackageScope.Create();
        await using var runtimeCatalogs = new FakeServiceCatalog<MemoryServices.IRuntimeCatalogService>(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.RuntimeCatalog));
        await using var embeddings = new FakeServiceCatalog<MemoryServices.IEmbeddingProviderService>(
            CurrentPublicRpcDescriptors.Get(RegressionTestBindings.EmbeddingProviders));
        var runtimeCatalog = ThrowingBusinessService.Create<MemoryServices.IRuntimeCatalogService>(unexpected);
        runtimeCatalogs.Add(runtimeCatalog);
        using var store = new MemoryLocalStore(package.Context);
        var settings = new MemorySemanticSettingsService(package.Context);
        using var resolver = new SemanticModelRuntimeResolver(runtimeCatalogs, embeddings, settings);
        var metrics = new SemanticMemoryMetricsService();
        var retrieval = new SemanticMemoryRetrievalBackend(store, resolver, settings);
        await using var indexing = new SemanticMemoryIndexingBackgroundService(store, resolver, settings, retrieval, metrics);
        var memory = new MemoryInspectorService(store, retrieval, indexing, resolver, metrics);
        var memoryRegistration = memory.CreateSunderRpcProviderRegistration();

        ThrowingBusinessService.Set(runtimeCatalog, new MemoryInspectorOperationException("expected"));
        var memoryAuthored = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeAsync(
            "sunder.package.agent.memory.semantic",
            memory,
            memoryRegistration,
            "sessions",
            "{}"));
        Assert.Equal(SunderRpcErrorKind.Domain, memoryAuthored.Error.Kind);
        Assert.Equal("memory.semantic.operation-failed", memoryAuthored.Error.Code);
        Assert.Equal("expected", memoryAuthored.Error.Message);
        Assert.DoesNotContain(UnexpectedMessage, memoryAuthored.Error.Message, StringComparison.Ordinal);

        ThrowingBusinessService.Set(runtimeCatalog, unexpected);
        Assert.Same(unexpected, await Assert.ThrowsAsync<InvalidOperationException>(() => InvokeAsync(
            "sunder.package.agent.memory.semantic",
            memory,
            memoryRegistration,
            "sessions",
            "{}")));

        var mcpCatalog = new McpServerCatalogService(package.Context);
        var mcpEditor = new McpSettingsEditorService(mcpCatalog);
        await using var mcpConnections = new McpClientConnectionManager(NullLoggerFactory.Instance);
        var mcpConnectionService = new McpServerConnectionService(mcpCatalog, mcpConnections, oauthService: null);
        await using var mcpConfiguration = new McpConfigurationCoordinator(
            new McpEcosystemConfigurationImporter(mcpCatalog),
            syncService: null);
        var mcp = new McpManagementService(
            mcpCatalog,
            mcpEditor,
            mcpConfiguration,
            mcpConnectionService,
            new McpOAuthCoordinator(oauthService: null, mcpConnections),
            package.Context);
        var mcpFailure = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeAsync(
            "sunder.package.agent.mcp",
            mcp,
            mcp.CreateSunderRpcProviderRegistration(),
            "delete",
            "\"\""));
        Assert.Equal(SunderRpcErrorKind.Domain, mcpFailure.Error.Kind);
        Assert.Equal("mcp.management.failed", mcpFailure.Error.Code);
        Assert.DoesNotContain(UnexpectedMessage, mcpFailure.Error.Message, StringComparison.Ordinal);

        static async Task InvokeAsync(
            string packageId,
            object implementation,
            SunderWorkerProviderRegistration registration,
            string method,
            string json)
        {
            using var state = new RegressionTestRpcState();
            state.AddGenerated(packageId, implementation, registration, NoOpLifetime.Instance, registration.ProviderId);
            var rpc = new RegressionTestRpcClient(state, packageId);
            var endpoint = Assert.Single((await rpc.DiscoverAsync(registration.ContractId)).Providers).Endpoint;
            await using var scope = await rpc.CreateCallScopeAsync();
            using var document = JsonDocument.Parse(json);
            await scope.InvokeAsync(
                endpoint,
                Assert.Single(CurrentPrivateRpcDescriptors.Get(registration.ContractId).Services).ServiceId,
                method,
                document.RootElement.Clone());
        }
    }

    private class ThrowingBusinessService : DispatchProxy
    {
        private Exception _error = null!;

        public ThrowingBusinessService() { }

        internal static T Create<T>(Exception error) where T : class
        {
            var service = Create<T, ThrowingBusinessService>();
            Set(service, error);
            return service;
        }

        internal static void Set(object service, Exception error)
            => ((ThrowingBusinessService)service)._error = error;

        protected override object? Invoke(MethodInfo? method, object?[]? arguments)
            => throw _error;
    }

    private sealed class NoOpLifetime : IDisposable
    {
        internal static NoOpLifetime Instance { get; } = new();

        public void Dispose() { }
    }

    [Fact]
    public void ExtensionOwnerMappings_PreserveOnlyAuthoredDomainMessages()
    {
        var skills = SkillOwnerJson.Map(new SkillOwnerOperationException("expected skills failure"));
        var subagents = SubagentOwnerJson.Map(
            new SubagentOwnerOperationException("expected subagents failure"));

        Assert.Equal(SunderRpcErrorKind.Domain, skills.Error.Kind);
        Assert.Equal("skills.owner.operation-failed", skills.Error.Code);
        Assert.Equal("expected skills failure", skills.Error.Message);
        Assert.DoesNotContain(UnexpectedMessage, skills.Error.Message, StringComparison.Ordinal);
        Assert.Equal(SunderRpcErrorKind.Domain, subagents.Error.Kind);
        Assert.Equal("subagents.owner.operation-failed", subagents.Error.Code);
        Assert.Equal("expected subagents failure", subagents.Error.Message);
        Assert.DoesNotContain(UnexpectedMessage, subagents.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AgentOwnerWorkerBoundary_MapsExpectedServiceRejectionsAndRemainsActive()
    {
        await using var boundary = new AgentOwnerBoundary();

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.ExecuteProfileCommandAsync(
                new AgentProfileCommand(
                    AgentProfileCommandKind.Save,
                    ProfileId: "missing-profile",
                    DisplayName: "Missing"),
                CancellationToken.None));

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.ExecuteWorkspaceCommandAsync(
                new AgentWorkspaceCommand(
                    AgentWorkspaceCommandKind.Save,
                    WorkspaceId: "missing-workspace",
                    DisplayName: "Missing"),
                CancellationToken.None));

        var firstWorkspaceResult = await boundary.Client.ExecuteWorkspaceCommandAsync(
            new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create, DisplayName: "First"),
            CancellationToken.None);
        var firstWorkspace = Assert.IsType<AgentWorkspaceRecord>(firstWorkspaceResult.Workspace);
        var secondWorkspaceResult = await boundary.Client.ExecuteWorkspaceCommandAsync(
            new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create, DisplayName: "Second"),
            CancellationToken.None);
        var secondWorkspace = Assert.IsType<AgentWorkspaceRecord>(secondWorkspaceResult.Workspace);

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.ExecuteSessionCommandAsync(
                new AgentSessionCommand(
                    AgentSessionCommandKind.Create,
                    Title: "Invalid workspace",
                    WorkspaceId: "missing-workspace"),
                CancellationToken.None));

        var sessionResult = await boundary.Client.ExecuteSessionCommandAsync(
            new AgentSessionCommand(
                AgentSessionCommandKind.Create,
                Title: "Session",
                WorkspaceId: firstWorkspace.WorkspaceId),
            CancellationToken.None);
        var session = Assert.IsType<AgentSessionRecord>(sessionResult.Session?.Session);

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.ExecuteRunCommandAsync(
                new AgentRunCommand(
                    AgentRunCommandKind.Start,
                    session.SessionId,
                    ProfileId: "profile",
                    UserMessage: "Expired upload",
                    WorkspaceId: firstWorkspace.WorkspaceId,
                    AttachmentHandles: [new("missing-transfer")],
                    UserTurnId: Guid.NewGuid()),
                CancellationToken.None));

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.ExecuteSessionCommandAsync(
                new AgentSessionCommand(
                    AgentSessionCommandKind.Update,
                    Session: session with { WorkspaceId = secondWorkspace.WorkspaceId }),
                CancellationToken.None));

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.ExecuteRunCommandAsync(
                new AgentRunCommand(
                    AgentRunCommandKind.Start,
                    session.SessionId,
                    ProfileId: "profile",
                    UserMessage: "Wrong workspace",
                    WorkspaceId: secondWorkspace.WorkspaceId,
                    UserTurnId: Guid.NewGuid()),
                CancellationToken.None));

        using (boundary.Services.GetRequiredService<AgentSessionDeletionFence>()
                   .Enter([session.SessionId], firstWorkspace.WorkspaceId))
        {
            _ = await AssertOperationFailedAsync(async () =>
                _ = await boundary.Client.ExecuteSessionCommandAsync(
                    new AgentSessionCommand(
                        AgentSessionCommandKind.Create,
                        Title: "Deletion race",
                        WorkspaceId: firstWorkspace.WorkspaceId),
                    CancellationToken.None));
            _ = await AssertOperationFailedAsync(async () =>
                _ = await boundary.Client.ExecuteRunCommandAsync(
                    new AgentRunCommand(
                        AgentRunCommandKind.Start,
                        session.SessionId,
                        ProfileId: "profile",
                        UserMessage: "Deletion race",
                        WorkspaceId: firstWorkspace.WorkspaceId,
                        UserTurnId: Guid.NewGuid()),
                    CancellationToken.None));
        }

        var dashboard = await boundary.Client.GetDashboardAsync(
            new AgentDashboardRequest(),
            CancellationToken.None);
        Assert.Contains(dashboard.Workspaces, item => item.WorkspaceId == firstWorkspace.WorkspaceId);
    }

    [Fact]
    public async Task AgentOwnerWorkerBoundary_LeavesUnknownFailuresGenericWithoutMessageLeak()
    {
        await using var boundary = new AgentOwnerBoundary(failCatalog: true);

        var exception = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            _ = await boundary.Client.GetCatalogAsync(
                new AgentCatalogRequest(),
                CancellationToken.None));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, exception.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", exception.Error.Code);
        Assert.Equal("The process provider handler failed.", exception.Error.Message);
        Assert.DoesNotContain(UnexpectedMessage, exception.Error.Message, StringComparison.Ordinal);

        var missingContent = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            _ = await boundary.Resources.DownloadAttachmentAsync(
                new AgentAttachmentMetadata(
                    Guid.NewGuid(),
                    UnexpectedMessage,
                    "application/octet-stream",
                    AgentAttachmentKind.Binary,
                    1,
                    new string('0', 64), // Valid metadata reaches the missing-content provider path.
                    "missing-content",
                    IsText: false,
                    WasTruncated: false),
                CancellationToken.None));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, missingContent.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", missingContent.Error.Code);
        Assert.DoesNotContain(UnexpectedMessage, missingContent.Error.Message, StringComparison.Ordinal);

        var dashboard = await boundary.Client.GetDashboardAsync(
            new AgentDashboardRequest(),
            CancellationToken.None);
        Assert.NotNull(dashboard);
    }

    private static async Task<SunderRpcException> AssertOperationFailedAsync(Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<SunderRpcException>(operation);
        Assert.Equal(SunderRpcErrorKind.Domain, exception.Error.Kind);
        Assert.Equal("agent.owner.operation-failed", exception.Error.Code);
        return exception;
    }

    private sealed class AgentOwnerBoundary : IAsyncDisposable
    {
        private readonly RegressionTestPackageScope _scope = RegressionTestPackageScope.Create();
        private readonly RegressionTestExtensionCatalog _catalog = new(workerStyleProviderBoundary: true);
        private readonly FakeServiceCatalog<AgentServices.ISelectableCapabilityProviderService>? _faultCatalog;
        private readonly AgentProfileService? _faultProfiles;

        public AgentOwnerBoundary(bool failCatalog = false)
        {
            Services = RegressionTestRuntime.Create(_scope.Context, _catalog);
            var owner = Services.GetRequiredService<AgentOwnerRuntimeProvider>();
            if (failCatalog)
            {
                // Substitute only the business dependency. The public owner call
                // still crosses the real generated provider/handler and broker.
                _faultCatalog = new(CurrentPublicRpcDescriptors.Get(RegressionTestBindings.SelectableCapabilityProviders));
                _faultCatalog.Add(ThrowingBusinessService.Create<AgentServices.ISelectableCapabilityProviderService>(
                    new InvalidOperationException(UnexpectedMessage)));
                _faultProfiles = ActivatorUtilities.CreateInstance<AgentProfileService>(Services, _faultCatalog);
                var handler = ActivatorUtilities.CreateInstance<global::Sunder.Package.Agent.Runtime.AgentCatalogHandler>(Services, _faultProfiles);
                owner = ActivatorUtilities.CreateInstance<AgentOwnerRuntimeProvider>(Services, handler);
            }
            Client = _catalog.AddOwnerRuntimeProvider(owner);
            Resources = new AgentOwnerResources(
                _catalog.GetServiceProviders<IAgentOwnerRuntimeProvider>(),
                _catalog.RpcForCaller("sunder.package.agent"));
        }

        public RegressionTestRuntime Services { get; }

        public RegressionTestExtensionCatalog Catalog => _catalog;

        public IAgentOwnerRuntimeProvider Client { get; }
        public AgentOwnerResources Resources { get; }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (_faultProfiles is not null) await _faultProfiles.DisposeAsync();
                await Services.DisposeAsync();
            }
            finally
            {
                if (_faultCatalog is not null) await _faultCatalog.DisposeAsync();
                _catalog.Dispose();
                _scope.Dispose();
            }
        }
    }
}
