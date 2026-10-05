using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyChildRunPersistenceAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        using var package = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var provider = new ChildRunChatProvider();
        catalog.AddProvider(RegressionTestBindings.ChatProviders, provider, "test.child.provider");
        var generated = await CreateCanonicalRuntime(assembly, package.Context, catalog);
        await using var lifetime = generated.Lifetime;
        var services = generated.Services;
        var sessions = services.GetRequiredService<AgentSessionService>();
        var profiles = services.GetRequiredService<AgentProfileService>();
        var workspaces = services.GetRequiredService<AgentWorkspaceService>();
        var workspace = workspaces.CreateWorkspace("Child run workspace");
        var root = sessions.CreateSession("Root", workspaceId: workspace.WorkspaceId);
        var parent = sessions.CreateSession("Parent", parentSessionId: root.SessionId, rootSessionId: root.SessionId, workspaceId: workspace.WorkspaceId);
        var parentProfile = await profiles.CreateProfileAsync("Visible parent profile");
        var profile = ChildProfile(parentProfile, "child.profile") with { DisplayName = "Child profile 漢字", IsInternal = false };
        var request = new AgentChildRunRequest(parent.SessionId, Guid.NewGuid(), 1, "call.one", workspace.WorkspaceId,
            " task.one ", profile, "First delegated task", "   ", "custom-child");
        await using var fixture = await CreateChildRunFixture(assembly, descriptor, services.GetRequiredService<AgentRunCoordinator>());
        var first = await fixture.Run(request).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(AgentRunStatus.Completed, first.Status);
        Assert.Equal("child reply 1 漢字", first.Content);
        Assert.Equal(profile.DisplayName, first.Title);
        var child = Assert.IsType<AgentSessionRecord>(sessions.GetSession(first.SessionId));
        Assert.Equal(parent.SessionId, child.ParentSessionId);
        Assert.Equal(root.SessionId, child.RootSessionId);
        Assert.Equal(request.ParentRunId, child.ParentRunId);
        Assert.Equal(1, child.ParentRunRevision);
        Assert.Equal("call.one", child.ParentToolCallId);
        Assert.Equal("task.one", child.TaskId);
        Assert.Equal("custom-child", child.AgentKind);
        Assert.Equal(workspace.WorkspaceId, child.WorkspaceId);
        Assert.True(profiles.GetProfile(profile.ProfileId)!.IsInternal);
        Assert.False(profile.IsInternal);
        Assert.DoesNotContain(profiles.ListProfiles(), item => item.ProfileId == profile.ProfileId);
        Assert.Equal(first.Summary, sessions.GetLatestCheckpoint(child.SessionId)!.Summary);
        Assert.Equal(1, sessions.GetLatestRun(child.SessionId)!.Key.RunRevision);

        var repeated = request with { TaskId = "TASK.ONE", ParentRunId = Guid.NewGuid(), ParentRunRevision = 2, ParentToolCallId = "call.two", UserMessage = "Second delegated task", Title = "Replacement title" };
        var second = await fixture.Run(repeated).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(first.SessionId, second.SessionId);
        Assert.Equal("child reply 2 漢字", second.Content);
        Assert.Equal(first.Title, second.Title);
        var reused = sessions.GetSession(child.SessionId)!;
        Assert.Equal(repeated.ParentRunId, reused.ParentRunId);
        Assert.Equal(2, reused.ParentRunRevision);
        Assert.Equal("call.two", reused.ParentToolCallId);
        Assert.Equal(2, sessions.GetLatestRun(child.SessionId)!.Key.RunRevision);
        Assert.Equal(2, sessions.ListTurns(child.SessionId).Count(turn => turn.Role == AgentMessageRole.User));
        Assert.Equal(2, sessions.ListTurns(child.SessionId).Count(turn => turn.Role == AgentMessageRole.Assistant));

        var untasked = request with { TaskId = null, Title = " Untasked " };
        var third = await fixture.Run(untasked).WaitAsync(TimeSpan.FromSeconds(15));
        var fourth = await fixture.Run(untasked).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(first.SessionId, third.SessionId);
        Assert.NotEqual(third.SessionId, fourth.SessionId);
        Assert.Null(sessions.GetSession(third.SessionId)!.TaskId);
        Assert.Equal("Untasked", third.Title);
        var otherProfile = await fixture.Run(request with { ChildProfile = ChildProfile(profile, "child.other") }).WaitAsync(TimeSpan.FromSeconds(15));
        var otherParent = await fixture.Run(request with { ParentSessionId = root.SessionId }).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.NotEqual(first.SessionId, otherProfile.SessionId);
        Assert.NotEqual(first.SessionId, otherParent.SessionId);
        Assert.Equal(root.SessionId, sessions.GetSession(otherParent.SessionId)!.ParentSessionId);
        Assert.Equal(6, provider.Clients.Count);
        Assert.All(provider.Clients, chat => { Assert.True(chat.Disposed); Assert.True(chat.EnumerationDisposed); });

        var count = sessions.ListSessions().Count;
        var missingParentProfile = ChildProfile(profile, "child.missing-parent");
        var missing = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Run(request with { ParentSessionId = Guid.NewGuid(), ChildProfile = missingParentProfile }));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, missing.Error.Kind);
        Assert.DoesNotContain("Parent session", missing.Message, StringComparison.Ordinal);
        Assert.Equal(count, sessions.ListSessions().Count);
        Assert.Null(profiles.GetProfile(missingParentProfile.ProfileId));

        var noProvider = ChildProfile(profile, "child.missing-provider") with { ChatProviderId = "missing.provider", ModelBindings = null };
        var failed = await fixture.Run(request with { ChildProfile = noProvider, TaskId = "missing-provider" }).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(AgentRunStatus.Failed, failed.Status);
        Assert.Null(failed.Content);
        Assert.Equal(failed.Summary, sessions.GetLatestCheckpoint(failed.SessionId)!.Summary);
        var noModel = ChildProfile(profile, "child.missing-model") with { ChatModelId = null, ModelBindings = null };
        Assert.Equal(AgentRunStatus.Failed, (await fixture.Run(request with { ChildProfile = noModel, TaskId = "missing-model" }).WaitAsync(TimeSpan.FromSeconds(15))).Status);
        provider.Readiness = new("child.provider", AgentProviderReadinessStatus.NeedsConfiguration, "Configure child provider");
        var notReady = await fixture.Run(request with { TaskId = "not-ready" }).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(AgentRunStatus.Failed, notReady.Status);
        Assert.Equal("Configure child provider", notReady.Summary);
        provider.Readiness = new("child.provider", AgentProviderReadinessStatus.Ready, "Ready");
        Assert.Equal(6, provider.Clients.Count);

        var wrongWorkspace = workspaces.CreateWorkspace("Other workspace");
        var workspaceError = await Assert.ThrowsAsync<SunderRpcException>(() => fixture.Run(request with { TaskId = "wrong-workspace", WorkspaceId = wrongWorkspace.WorkspaceId }));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, workspaceError.Error.Kind);
        var rejectedChild = Assert.Single(sessions.ListSessions(), value => value.TaskId == "wrong-workspace");
        Assert.Equal(workspace.WorkspaceId, rejectedChild.WorkspaceId);
        Assert.Empty(sessions.ListTurns(rejectedChild.SessionId));
        Assert.Null(sessions.GetLatestRun(rejectedChild.SessionId));
        Assert.Equal(workspace.WorkspaceId, sessions.GetSession(parent.SessionId)!.WorkspaceId);

        await VerifyChildRunCancellationAsync(fixture, sessions, profiles, services.GetRequiredService<AgentActiveRunRegistry>(), provider, request);
    }

    private static AgentProfileRecord ChildProfile(AgentProfileRecord profile, string id)
        => profile with { ProfileId = id, ModelBindings = profile.ModelBindings?.Select(binding => binding with { ProfileId = id }).ToArray() };

    private sealed class ChildRunChatProvider : IAgentChatProvider
    {
        public AgentProviderDescriptor Descriptor { get; } = new("child.provider", "Child provider", [], true, true);
        public AgentProviderReadiness Readiness { get; set; } = new("child.provider", AgentProviderReadinessStatus.Ready, "Ready");
        public bool BlockReadiness { get; set; }
        public bool BlockStream { get; set; }
        public TaskCompletionSource ReadinessStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<RunControlChatProbe> Clients { get; } = [];
        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([new("child.model", "Child model", 16000, 1000, true)]);
        public async ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (BlockReadiness) { ReadinessStarted.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); }
            return Readiness;
        }
        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderRunCapabilities(true, true, true, "Child capabilities", ContextWindowTokens: 16000, MaxOutputTokens: 1000));
        public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal("child.model", context.ModelId);
            var chat = new RunControlChatProbe([new(ChatRole.Assistant, BlockStream ? "partial-child" : $"  child reply {Clients.Count + 1} 漢字  ") { FinishReason = BlockStream ? null : ChatFinishReason.Stop }])
            { BlockAfterUpdate = BlockStream };
            Clients.Add(chat);
            return ValueTask.FromResult<IChatClient>(chat);
        }
    }
}
