using System.Reflection;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.DependencyInjection;
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
    private static async Task VerifyDefaultBehaviorAsync(Assembly assembly, IReadOnlyDictionary<string, SunderRpcContractDescriptor> descriptors)
    {
        foreach (var providerFailure in new[] { false, true })
        {
            using var package = RegressionTestPackageScope.Create();
            using var catalog = new RegressionTestExtensionCatalog();
            var generated = await CreateCanonicalRuntime(assembly, package.Context, catalog);
            await using var lifetime = generated.Lifetime;
            var services = generated.Services;
            var sessions = services.GetRequiredService<AgentSessionService>();
            var store = services.GetRequiredService<AgentLocalStore>();
            var activeRuns = services.GetRequiredService<AgentActiveRunRegistry>();
            var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Generated behavior loop");
            var profile = await services.GetRequiredService<AgentProfileService>().CreateProfileAsync("Behavior profile");
            var session = sessions.CreateSession("Generated loop run", profileId: profile.ProfileId, workspaceId: workspace.WorkspaceId);
            var reserved = sessions.ReserveRun(session.SessionId, profile.ProfileId, "Hello behavior loop");
            var started = store.TryStartRun(reserved.Key, reserved.Epoch, reserved.UserMessage, [], null, "Running")!;
            var run = started.Transition.Run;
            using var cancellation = new CancellationTokenSource();
            activeRuns.Activate(session.SessionId, new(run.Key.RunId, run.Key.RunRevision, run.StartedAtUtc, profile.ProfileId, run.UserMessage, cancellation)
            { DurableLease = new AgentDurableRunLease(run) });
            try
            {
                var chat = new RunControlChatProbe(providerFailure ? [] : [new(ChatRole.Assistant, "Generated loop answer 漢字") { FinishReason = ChatFinishReason.Stop }])
                {
                    Failure = providerFailure ? new AgentChatProviderException("Request rejected", "Safe rejection content", "test.provider-rejected") : null,
                };
                var provider = new RunControlProvider(() => chat);
                var host = services.GetRequiredService<AgentBehaviorLoopHostFactory>().Create(provider, session, profile, workspace,
                    run.Key.RunId, run.Key.RunRevision, run.StartedAtUtc, run.UserMessage, started.UserTurn.TurnId, null);
                var loop = services.GetRequiredService<DefaultAgentBehaviorLoop>();
                await using var fixture = await CreateBehaviorFixture(assembly, descriptors, loop, "sunder.package.agent");
                var bridge = fixture.Bridge;
                Assert.Equal(DefaultAgentBehaviorLoop.LoopId, bridge.Descriptor.LoopId);
                var context = new AgentBehaviorLoopContext(session, profile, "provider.one", "model.one",
                    new(true, true, true, "Test capabilities", ContextWindowTokens: 16000, MaxOutputTokens: 1000), workspace, null,
                    run.Key.RunId, run.Key.RunRevision, started.Transition.Checkpoint, run.StartedAtUtc, run.UserMessage, started.UserTurn.TurnId);
                var result = await bridge.RunAsync(context, host).AsTask().WaitAsync(TimeSpan.FromSeconds(15));
                Assert.Equal(providerFailure ? AgentBehaviorLoopCompletionKind.Failed : AgentBehaviorLoopCompletionKind.Completed, result.CompletionKind);
                Assert.Equal(providerFailure ? AgentRunStatus.Failed : AgentRunStatus.Completed, result.Checkpoint.Status);
                Assert.Equal(result.Checkpoint.CheckpointId, sessions.GetLatestCheckpoint(session.SessionId)!.CheckpointId);
                var assistant = Assert.Single(sessions.ListTurns(session.SessionId), value => value.Role == AgentMessageRole.Assistant);
                Assert.False(assistant.IsStreaming);
                Assert.Equal(providerFailure ? "Safe rejection content" : "Generated loop answer 漢字", Assert.Single(assistant.Items).TextContent);
                var budget = store.GetRun(run.Key.RunId)!.BudgetState;
                Assert.Equal(1, budget.ProviderCycles);
                Assert.Equal(0, budget.ToolCalls);
                Assert.True(budget.SubmittedContextTokens > 0);
                Assert.True(chat.Disposed);
                Assert.True(chat.EnumerationDisposed);
                Assert.Contains(chat.Messages, message => message.Role == ChatRole.User && message.Text.Contains("Hello behavior loop", StringComparison.Ordinal));
                Assert.Equal("model.one", Assert.IsType<AgentChatClientContext>(provider.LastContext).ModelId);
                Assert.Equal("provider.one", provider.LastContext!.ProviderId);
                Assert.Throws<SunderRpcException>(() => fixture.Registry.Resolve(fixture.Owner.LastRun!.InvocationId, "sunder.package.agent"));
            }
            finally { activeRuns.Complete(session.SessionId, run.Key.RunId, run.Key.RunRevision); }
        }
    }
}
