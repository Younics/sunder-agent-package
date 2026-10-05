extern alias SkillsWorker;
extern alias SubagentsWorker;
extern alias BuilderWorker;
extern alias McpWorker;
extern alias MemoryWorker;
extern alias AgentApp;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using AgentApp::Sunder.Package.Agent.Contracts.Models;
using BuilderWorker::Sunder.Package.Agent.Builder;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Runtime;
using IAgentOwnerClient = AgentApp::Sunder.Package.Agent.Runtime.IAgentOwnerClient;
using Sunder.Package.Agent.Protocol;
using AgentApp::Sunder.Package.Agent.Runtime;
using AgentOwnerRuntimeProvider = Sunder.Package.Agent.Runtime.AgentOwnerRuntimeProvider;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using System.Reflection;
using System.Text.Json;
using Xunit;
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

        await Verify(
            error => new McpWorker::Sunder.Package.Agent.Mcp.Rpc.ManagementService(ThrowingOperations.Create<IMcpOwnerManagementClient>(error)).CreateSunderRpcProviderRegistration(),
            new McpManagementOperationException("expected"), "mcp.management.failed", "sunder.package.agent.mcp", "query", "{}", mcpContent: true);
        await Verify(
            error => new BuilderWorker::Sunder.Package.Agent.Builder.BuilderRuntimeService(ThrowingOperations.Create<IBuilderRuntimeOperations>(error)).CreateSunderRpcProviderRegistration(),
            new BuilderRpcDomainException("agent.builder.invalid-request", "expected"), "agent.builder.invalid-request", "sunder.package.agent.builder", "execute",
            "{\"kind\":\"list-workspaces\",\"timeoutSeconds\":1}");
        await Verify(
            error => new Rpc.Owner.OwnerRuntimeService(ThrowingOperations.Create<Runtime.IAgentOwnerRuntimeProvider>(error)).CreateSunderRpcProviderRegistration(),
            new Runtime.AgentOwnerOperationException("expected"), "agent.owner.operation-failed", "sunder.package.agent", "dashboard", "{}");
        await Verify(
            error => new MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Rpc.ManagementService(ThrowingOperations.Create<IMemoryInspectorRpcService>(error)).CreateSunderRpcProviderRegistration(),
            new MemoryInspectorOperationException("expected"), "memory.semantic.operation-failed", "sunder.package.agent.memory.semantic", "query", "{\"kind\":\"sessions\",\"includeInactive\":false}");

        async Task Verify(Func<Exception, SunderWorkerProviderRegistration> create, Exception expected, string code,
            string packageId, string method, string json, bool mcpContent = false)
        {
            Assert.Same(unexpected, await Assert.ThrowsAsync<InvalidOperationException>(() => Invoke(create(unexpected))));
            var mapped = await Assert.ThrowsAsync<SunderRpcException>(() => Invoke(create(expected)));
            Assert.Equal(SunderRpcErrorKind.Domain, mapped.Error.Kind);
            Assert.Equal(code, mapped.Error.Code);
            Assert.Equal("expected", mapped.Error.Message);
            Assert.DoesNotContain(UnexpectedMessage, mapped.Error.Message, StringComparison.Ordinal);

            async Task Invoke(SunderWorkerProviderRegistration registration)
            {
                using var state = new RegressionTestRpcState();
                state.AddGenerated(packageId, registration.Handler, registration, NoOpLifetime.Instance, registration.ProviderId);
                var rpc = new RegressionTestRpcClient(state, packageId);
                var endpoint = Assert.Single((await rpc.DiscoverAsync(registration.ContractId)).Providers).Endpoint;
                await using var scope = await rpc.CreateCallScopeAsync();
                using var document = JsonDocument.Parse(json);
                var request = document.RootElement.Clone();
                if (mcpContent)
                {
                    var content = await McpManagementContent.RegisterAsync(new McpQuery(McpQueryKind.Catalog), "query.json", scope.DeadlineUtc,
                        (stream, options, token) => scope.RegisterContentAsync(endpoint, stream, options, token), CancellationToken.None);
                    request = JsonSerializer.SerializeToElement(new { content }, RegressionTestRpcValues.Json);
                }
                await scope.InvokeAsync(endpoint, Assert.Single(RegressionTestRpcDescriptors.Get(registration.ContractId).Services).ServiceId, method, request);
            }
        }
    }

    private class ThrowingOperations : DispatchProxy
    {
        private Exception _error = null!;
        internal static T Create<T>(Exception error) where T : class
        {
            var value = Create<T, ThrowingOperations>();
            ((ThrowingOperations)(object)value)._error = error;
            return value;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? arguments) => throw _error;
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
            _ = await boundary.Client.InvokeAsync(
                AgentRuntimeOperations.Profiles,
                new AgentProfileCommand(
                    AgentProfileCommandKind.Save,
                    ProfileId: "missing-profile",
                    DisplayName: "Missing"),
                CancellationToken.None));

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.InvokeAsync(
                AgentRuntimeOperations.Workspaces,
                new AgentWorkspaceCommand(
                    AgentWorkspaceCommandKind.Save,
                    WorkspaceId: "missing-workspace",
                    DisplayName: "Missing"),
                CancellationToken.None));

        var firstWorkspaceResult = await boundary.Client.InvokeAsync(
            AgentRuntimeOperations.Workspaces,
            new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create, DisplayName: "First"),
            CancellationToken.None);
        var firstWorkspace = Assert.IsType<AgentWorkspaceRecord>(firstWorkspaceResult.Workspace);
        var secondWorkspaceResult = await boundary.Client.InvokeAsync(
            AgentRuntimeOperations.Workspaces,
            new AgentWorkspaceCommand(AgentWorkspaceCommandKind.Create, DisplayName: "Second"),
            CancellationToken.None);
        var secondWorkspace = Assert.IsType<AgentWorkspaceRecord>(secondWorkspaceResult.Workspace);

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.InvokeAsync(
                AgentRuntimeOperations.SessionCommands,
                new AgentSessionCommand(
                    AgentSessionCommandKind.Create,
                    Title: "Invalid workspace",
                    WorkspaceId: "missing-workspace"),
                CancellationToken.None));

        var sessionResult = await boundary.Client.InvokeAsync(
            AgentRuntimeOperations.SessionCommands,
            new AgentSessionCommand(
                AgentSessionCommandKind.Create,
                Title: "Session",
                WorkspaceId: firstWorkspace.WorkspaceId),
            CancellationToken.None);
        var session = Assert.IsType<AgentSessionRecord>(sessionResult.Session?.Session);

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.InvokeAsync(
                AgentRuntimeOperations.Runs,
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
            _ = await boundary.Client.InvokeAsync(
                AgentRuntimeOperations.SessionCommands,
                new AgentSessionCommand(
                    AgentSessionCommandKind.Update,
                    Session: session with { WorkspaceId = secondWorkspace.WorkspaceId }),
                CancellationToken.None));

        _ = await AssertOperationFailedAsync(async () =>
            _ = await boundary.Client.InvokeAsync(
                AgentRuntimeOperations.Runs,
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
                _ = await boundary.Client.InvokeAsync(
                    AgentRuntimeOperations.SessionCommands,
                    new AgentSessionCommand(
                        AgentSessionCommandKind.Create,
                        Title: "Deletion race",
                        WorkspaceId: firstWorkspace.WorkspaceId),
                    CancellationToken.None));
            _ = await AssertOperationFailedAsync(async () =>
                _ = await boundary.Client.InvokeAsync(
                    AgentRuntimeOperations.Runs,
                    new AgentRunCommand(
                        AgentRunCommandKind.Start,
                        session.SessionId,
                        ProfileId: "profile",
                        UserMessage: "Deletion race",
                        WorkspaceId: firstWorkspace.WorkspaceId,
                        UserTurnId: Guid.NewGuid()),
                    CancellationToken.None));
        }

        var dashboard = await boundary.Client.InvokeAsync(
            AgentRuntimeOperations.Dashboard,
            new AgentDashboardRequest(),
            CancellationToken.None);
        Assert.Contains(dashboard.Workspaces, item => item.WorkspaceId == firstWorkspace.WorkspaceId);
    }

    [Fact]
    public async Task AgentOwnerWorkerBoundary_LeavesUnknownFailuresGenericWithoutMessageLeak()
    {
        await using var boundary = new AgentOwnerBoundary();
        boundary.Catalog.AddProvider(
            RegressionTestBindings.SelectableCapabilityProviders,
            new ThrowingSelectableCapabilityProvider());

        var exception = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            _ = await boundary.Client.InvokeAsync(
                AgentRuntimeOperations.Catalog,
                new AgentCatalogRequest(),
                CancellationToken.None));

        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, exception.Error.Kind);
        Assert.Equal("rpc.provider.handler-fault", exception.Error.Code);
        Assert.Equal("The process provider handler failed.", exception.Error.Message);
        Assert.DoesNotContain(UnexpectedMessage, exception.Error.Message, StringComparison.Ordinal);

        var missingContent = await Assert.ThrowsAsync<SunderRpcException>(async () =>
            _ = await boundary.Client.DownloadAttachmentAsync(
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

        var dashboard = await boundary.Client.InvokeAsync(
            AgentRuntimeOperations.Dashboard,
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

        public AgentOwnerBoundary()
        {
            Services = RegressionTestRuntime.Create(_scope.Context, _catalog);
            Client = _catalog.AddOwnerRuntimeProvider(
                Services.GetRequiredService<AgentOwnerRuntimeProvider>());
        }

        public RegressionTestRuntime Services { get; }

        public RegressionTestExtensionCatalog Catalog => _catalog;

        public IAgentOwnerClient Client { get; }

        public async ValueTask DisposeAsync()
        {
            await Services.DisposeAsync();
            _catalog.Dispose();
            _scope.Dispose();
        }
    }

    private sealed class ThrowingSelectableCapabilityProvider
        : IAgentProfileSelectableCapabilityProvider
    {
        public string ProviderId => "test.throwing.selectable-capabilities";

        public string DisplayName => throw new InvalidOperationException(UnexpectedMessage);

        public ValueTask<IReadOnlyList<Sunder.Package.Agent.Contracts.Models.AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
            Sunder.Package.Agent.Contracts.Models.AgentProfileSelectableCapabilityRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<Sunder.Package.Agent.Contracts.Models.AgentProfileSelectableCapabilityDescriptor>>([]);
    }
}
