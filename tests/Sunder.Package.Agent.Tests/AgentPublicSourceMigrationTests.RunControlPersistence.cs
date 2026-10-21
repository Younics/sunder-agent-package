using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyRunControlPersistenceAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        using var package = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var generated = await CreateCanonicalRuntime(assembly, package.Context, catalog);
        await using var lifetime = generated.Lifetime;
        var services = generated.Services;
        var sessions = services.GetRequiredService<AgentSessionService>();
        var store = services.GetRequiredService<AgentLocalStore>();
        var activeRuns = services.GetRequiredService<AgentActiveRunRegistry>();
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Run control source");
        var profile = await services.GetRequiredService<AgentProfileService>().CreateProfileAsync("Run control profile");
        var session = sessions.CreateSession("Source run", profileId: profile.ProfileId, workspaceId: workspace.WorkspaceId);
        var reserved = sessions.ReserveRun(session.SessionId, profile.ProfileId, "User message");
        var started = store.TryStartRun(reserved.Key, reserved.Epoch, "User message", [], null, "Running")!;
        var run = started.Transition.Run;
        using var cancellation = new CancellationTokenSource();
        var durableLease = new AgentDurableRunLease(run);
        activeRuns.Activate(session.SessionId, new(run.Key.RunId, run.Key.RunRevision, run.StartedAtUtc, profile.ProfileId, run.UserMessage, cancellation) { DurableLease = durableLease });
        await using var chatProviders = new FakeServiceCatalog<AgentServices.IChatProviderService>(CurrentPublicRpcDescriptors.Get(RegressionTestBindings.ChatProviders));
        var provider = new RunControlProvider();
        var host = services.GetRequiredService<AgentBehaviorLoopHostFactory>().Create(new(null, chatProviders.Add(provider), provider.Descriptor), session, profile, workspace,
            run.Key.RunId, run.Key.RunRevision, run.StartedAtUtc, run.UserMessage, started.UserTurn.TurnId, null);
        await using var fixture = await CreateRunControlFixture(assembly, descriptor);
        using var lease = fixture.Registry.Open(host, RunControlCaller, out var invocationId);
        var control = fixture.Client;
        Assert.True(await control.IsCurrentAsync(invocationId));
        Assert.Single(await control.ListTurnsAsync(invocationId));
        var charge = new AgentRunBudgetCharge(2, 3, 4000);
        await control.ChargeRunBudgetAsync(invocationId, charge);
        await control.ChargeRunBudgetAsync(invocationId, charge);
        Assert.Equal(new AgentRunBudgetState(4, 6, 8000), store.GetRun(run.Key.RunId)!.BudgetState);
        Assert.Equal(8000, (await control.GetRunBudgetAsync(invocationId)).SubmittedContextTokens);
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => control.ChargeRunBudgetAsync(invocationId, charge, canceled.Token).AsTask());
            Assert.Equal(new AgentRunBudgetState(4, 6, 8000), store.GetRun(run.Key.RunId)!.BudgetState);
        }
        await control.SelectSessionContextAsync(invocationId, new([started.UserTurn], false, 0));
        Assert.NotNull(await control.BuildInstructionContextAsync(invocationId));
        await control.AcknowledgePromptContextAsync(invocationId, []);
        await control.DiscardPromptContextAsync(invocationId);
        await control.ReportActivityAsync(invocationId, AgentRunActivityKind.Thinking, "Working");
        Assert.Empty(await control.ListReadyToolsAsync(invocationId));
        Assert.Empty(await control.InvokeToolsAsync(invocationId, [], null));

        var turn = await control.UpsertAssistantTurnAsync(invocationId, null, "First");
        Assert.True(turn.IsStreaming);
        var updated = await control.UpsertAssistantTurnAsync(invocationId, turn, "First 漢字");
        Assert.Equal(turn.TurnId, updated.TurnId);
        Assert.True(updated.ContentRevision > turn.ContentRevision);
        var completed = await control.CompleteAssistantTurnAsync(invocationId, updated);
        Assert.False(completed.IsStreaming);
        Assert.Equal("First 漢字", Assert.Single(sessions.GetTurn(completed.TurnId)!.Items).TextContent);
        await control.PublishLifecycleAsync(invocationId, AgentLifecycleEventKind.AssistantTurnCompleted, AgentRunStatus.Running, completed, null, false);

        var open = await control.UpsertAssistantTurnAsync(invocationId, null, "Open at terminal");
        var terminal = await control.SaveCheckpointAsync(invocationId, AgentRunStatus.Completed, "Done");
        Assert.Equal(AgentRunStatus.Completed, sessions.GetLatestCheckpoint(session.SessionId)!.Status);
        Assert.False(sessions.GetTurn(open.TurnId)!.IsStreaming);
        Assert.Equal(terminal.CheckpointId, (await control.SaveCheckpointAsync(invocationId, AgentRunStatus.Completed, "Repeated")).CheckpointId);
        var turnsBefore = RunJson(sessions.ListTurns(session.SessionId));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => control.UpsertAssistantTurnAsync(invocationId, null, "Rejected after terminal").AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
        Assert.True(JsonElement.DeepEquals(turnsBefore, RunJson(sessions.ListTurns(session.SessionId))));
        activeRuns.Complete(session.SessionId, run.Key.RunId, run.Key.RunRevision);
        Assert.False(await control.IsCurrentAsync(invocationId));

        // A different actor can revoke the durable epoch while the invocation lease still exists.
        var next = sessions.ReserveRun(session.SessionId, profile.ProfileId, "Next run");
        var nextStarted = store.TryStartRun(next.Key, next.Epoch, "Next run", [], null, "Running")!;
        var nextProvider = new RunControlProvider();
        var nextHost = services.GetRequiredService<AgentBehaviorLoopHostFactory>().Create(new(null, chatProviders.Add(nextProvider), nextProvider.Descriptor), session, profile, workspace,
            next.Key.RunId, next.Key.RunRevision, next.StartedAtUtc, next.UserMessage, nextStarted.UserTurn.TurnId, null);
        using var nextLease = fixture.Registry.Open(nextHost, RunControlCaller, out var nextId);
        Assert.NotNull(store.TryTransitionRun(nextStarted.Transition.Run.Key, nextStarted.Transition.Run.Epoch, AgentRunStatus.Stopped, "External stop"));
        var budgetBefore = store.GetRun(next.Key.RunId)!.BudgetState;
        await Assert.ThrowsAsync<SunderRpcException>(() => control.ChargeRunBudgetAsync(nextId, new(1, 1, 1)).AsTask());
        Assert.Equal(budgetBefore, store.GetRun(next.Key.RunId)!.BudgetState);

    }

    private sealed class RunControlProvider(Func<IChatClient>? create = null) : AgentServices.IChatProviderService
    {
        public AgentChatClientContext? LastContext { get; private set; }
        public AgentProviderDescriptor Descriptor { get; } = new("provider.one", "Provider", [], true, true);
        public ValueTask<AgentProviderDescriptor> DescribeAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(Descriptor);
        public ValueTask<IReadOnlyList<AgentModelDescriptor>> ListModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);
        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(Descriptor.ProviderId, AgentProviderReadinessStatus.Ready, "Ready"));
        public ValueTask<AgentProviderRunCapabilities> GetCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderRunCapabilities(true, true, true, "Test capabilities"));
        public ValueTask<string?> ResolveUtilityModelAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult<string?>(null);
        private ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTask.FromResult(create?.Invoke() ?? new RunControlChatProbe([new(ChatRole.Assistant, "Done")]));
        }
        public async IAsyncEnumerable<AgentChatStreamEvent> StreamChatAsync(AgentChatStreamRequest request,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            using var client = await CreateChatClientAsync(new(Descriptor.ProviderId, request.ModelId), cancellationToken);
            var messages = await AgentChatMapper.ToChatMessagesAsync(request.Messages, cancellationToken);
            await foreach (var update in client.GetStreamingResponseAsync(messages, AgentChatMapper.ToChatOptions(request.Options), cancellationToken)
                               .WithCancellation(cancellationToken))
                yield return AgentChatMapper.FromUpdate(update);
        }
    }
}
