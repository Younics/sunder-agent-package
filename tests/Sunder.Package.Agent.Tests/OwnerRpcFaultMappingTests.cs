extern alias SkillsWorker;
extern alias SubagentsWorker;

using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Builder;
using Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Package.Agent.Mcp.Services;
using Sunder.Package.Agent.Memory.Semantic.Runtime;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Xunit;
using SkillOwnerJson = SkillsWorker::Sunder.Package.Agent.Skills.Runtime.OwnerJson;
using SkillOwnerOperationException = SkillsWorker::Sunder.Package.Agent.Skills.Runtime.SkillOwnerOperationException;
using SubagentOwnerJson = SubagentsWorker::Sunder.Package.Agent.Subagents.Runtime.SubagentOwnerJson;

namespace Sunder.Package.Agent.Tests;

public sealed class OwnerRpcFaultMappingTests
{
    private const string UnexpectedMessage = "unexpected-provider-secret-canary";

    [Fact]
    public void OwnerAdapters_MapOnlyExplicitDomainExceptions()
    {
        var unexpected = new InvalidOperationException(UnexpectedMessage);

        Assert.False(McpManagementRpcProvider.ShouldMap(unexpected));
        Assert.False(BuilderRpcProvider.ShouldMap(unexpected));
        Assert.False(SkillOwnerJson.ShouldMap(unexpected));
        Assert.False(SubagentOwnerJson.ShouldMap(unexpected));
        Assert.False(AgentOwnerRuntimeRpc.ShouldMap(unexpected));
        Assert.False(MemoryInspectorRpc.ShouldMap(unexpected));

        Assert.True(McpManagementRpcProvider.ShouldMap(new McpManagementOperationException("expected")));
        Assert.True(BuilderRpcProvider.ShouldMap(new BuilderRpcDomainException("agent.builder.invalid-request", "expected")));
        Assert.True(SkillOwnerJson.ShouldMap(new SkillOwnerOperationException("expected")));
        Assert.True(SubagentOwnerJson.ShouldMap(new SubagentOwnerOperationException("expected")));
        Assert.True(AgentOwnerRuntimeRpc.ShouldMap(new AgentOwnerOperationException("expected")));
        Assert.True(MemoryInspectorRpc.ShouldMap(new MemoryInspectorOperationException("expected")));
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
            AgentRpcServices.SelectableCapabilityProviders,
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
                    string.Empty,
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
            var services = new ServiceCollection();
            services.AddSingleton<AgentRpcCatalog>(_catalog);
            new PackageModule().ConfigureRuntimeServices(services, _scope.Context);
            Services = services.BuildServiceProvider();
            Client = _catalog.AddOwnerRuntimeProvider(
                Services.GetRequiredService<AgentOwnerRuntimeProvider>());
        }

        public ServiceProvider Services { get; }

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

        public ValueTask<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>> ListCapabilitiesAsync(
            AgentProfileSelectableCapabilityRequest request,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentProfileSelectableCapabilityDescriptor>>([]);
    }
}
