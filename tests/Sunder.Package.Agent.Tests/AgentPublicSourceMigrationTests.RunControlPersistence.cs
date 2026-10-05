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
        var host = services.GetRequiredService<AgentBehaviorLoopHostFactory>().Create(new RunControlProvider(), session, profile, workspace,
            run.Key.RunId, run.Key.RunRevision, run.StartedAtUtc, run.UserMessage, started.UserTurn.TurnId, null);
        await using var fixture = await CreateRunControlFixture(assembly, descriptor);
        using var lease = fixture.Registry.Open(host, RunControlCaller, out var invocationId);
        var invocation = new RunControlInvocation(invocationId);
        Assert.True((await Call("IsCurrentAsync", invocation)).GetBoolean());
        Assert.Single((await Call("ListTurnsAsync", invocation)).EnumerateArray());
        var charge = new RunControlBudgetChargeRequest(invocation, new(2, 3, 4000));
        await Call("ChargeRunBudgetAsync", charge);
        await Call("ChargeRunBudgetAsync", charge);
        Assert.Equal(new AgentRunBudgetState(4, 6, 8000), store.GetRun(run.Key.RunId)!.BudgetState);
        Assert.Equal(8000, (await Call("GetRunBudgetAsync", invocation)).GetProperty("submittedContextTokens").GetInt64());
        using (var canceled = new CancellationTokenSource())
        {
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeGeneratedClient(fixture.Client, "ChargeRunBudgetAsync", RunJson(charge), canceled.Token));
            Assert.Equal(new AgentRunBudgetState(4, 6, 8000), store.GetRun(run.Key.RunId)!.BudgetState);
        }
        await Call("SelectSessionContextAsync", new RunControlSessionProjectionRequest(invocation, new([started.UserTurn], false, 0)));
        var context = await Call("BuildInstructionContextAsync", invocation);
        Assert.Equal(JsonValueKind.Object, context.ValueKind);
        await Call("AcknowledgePromptContextAsync", new RunControlPromptContextRequest(invocation, []));
        await Call("DiscardPromptContextAsync", invocation);
        await Call("ReportActivityAsync", new RunControlActivityRequest(invocation, AgentRunActivityKind.Thinking, "Working"));
        Assert.Empty((await Call("ListReadyToolsAsync", invocation)).EnumerateArray());
        Assert.Empty((await Call("InvokeToolsAsync", new RunControlToolsRequest(invocation, [], null))).EnumerateArray());

        var first = await Call("UpsertAssistantTurnAsync", new RunControlAssistantTurnRequest(invocation, null, "First"));
        var turn = first.Deserialize<AgentTurnRecord>(ExecutionJson)!;
        Assert.True(turn.IsStreaming);
        var updated = (await Call("UpsertAssistantTurnAsync", new RunControlAssistantTurnRequest(invocation, turn, "First 漢字"))).Deserialize<AgentTurnRecord>(ExecutionJson)!;
        Assert.Equal(turn.TurnId, updated.TurnId);
        Assert.True(updated.ContentRevision > turn.ContentRevision);
        var completed = (await Call("CompleteAssistantTurnAsync", new RunControlCompleteTurnRequest(invocation, updated))).Deserialize<AgentTurnRecord>(ExecutionJson)!;
        Assert.False(completed.IsStreaming);
        Assert.Equal("First 漢字", Assert.Single(sessions.GetTurn(completed.TurnId)!.Items).TextContent);
        await Call("PublishLifecycleAsync", new RunControlLifecycleRequest(invocation, AgentLifecycleEventKind.AssistantTurnCompleted, AgentRunStatus.Running, completed, null, false));

        var open = (await Call("UpsertAssistantTurnAsync", new RunControlAssistantTurnRequest(invocation, null, "Open at terminal"))).Deserialize<AgentTurnRecord>(ExecutionJson)!;
        var terminal = (await Call("SaveCheckpointAsync", new RunControlCheckpointRequest(invocation, AgentRunStatus.Completed, "Done"))).Deserialize<AgentRunCheckpointRecord>(ExecutionJson)!;
        Assert.Equal(AgentRunStatus.Completed, sessions.GetLatestCheckpoint(session.SessionId)!.Status);
        Assert.False(sessions.GetTurn(open.TurnId)!.IsStreaming);
        Assert.Equal(terminal.CheckpointId, (await Call("SaveCheckpointAsync", new RunControlCheckpointRequest(invocation, AgentRunStatus.Completed, "Repeated"))).GetProperty("checkpointId").GetGuid());
        var turnsBefore = RunJson(sessions.ListTurns(session.SessionId));
        var error = await Assert.ThrowsAsync<SunderRpcException>(() => Call("UpsertAssistantTurnAsync", new RunControlAssistantTurnRequest(invocation, null, "Rejected after terminal")));
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, error.Error.Kind);
        Assert.True(JsonElement.DeepEquals(turnsBefore, RunJson(sessions.ListTurns(session.SessionId))));
        activeRuns.Complete(session.SessionId, run.Key.RunId, run.Key.RunRevision);
        Assert.False((await Call("IsCurrentAsync", invocation)).GetBoolean());

        // A different actor can revoke the durable epoch while the invocation lease still exists.
        var next = sessions.ReserveRun(session.SessionId, profile.ProfileId, "Next run");
        var nextStarted = store.TryStartRun(next.Key, next.Epoch, "Next run", [], null, "Running")!;
        var nextHost = services.GetRequiredService<AgentBehaviorLoopHostFactory>().Create(new RunControlProvider(), session, profile, workspace,
            next.Key.RunId, next.Key.RunRevision, next.StartedAtUtc, next.UserMessage, nextStarted.UserTurn.TurnId, null);
        using var nextLease = fixture.Registry.Open(nextHost, RunControlCaller, out var nextId);
        Assert.NotNull(store.TryTransitionRun(nextStarted.Transition.Run.Key, nextStarted.Transition.Run.Epoch, AgentRunStatus.Stopped, "External stop"));
        var budgetBefore = store.GetRun(next.Key.RunId)!.BudgetState;
        await Assert.ThrowsAsync<SunderRpcException>(() => Call("ChargeRunBudgetAsync", new RunControlBudgetChargeRequest(new(nextId), new(1, 1, 1))));
        Assert.Equal(budgetBefore, store.GetRun(next.Key.RunId)!.BudgetState);

        Task<JsonElement> Call<T>(string method, T request) => InvokeGeneratedClient(fixture.Client, method, RunJson(request));
    }

    private sealed class RunControlProvider(Func<IChatClient>? create = null) : IAgentChatProvider
    {
        public AgentChatClientContext? LastContext { get; private set; }
        public AgentProviderDescriptor Descriptor { get; } = new("provider.one", "Provider", [], true, true);
        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);
        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
        {
            LastContext = context;
            return ValueTask.FromResult(create?.Invoke() ?? new RunControlChatProbe([new(ChatRole.Assistant, "Done")]));
        }
    }
}
