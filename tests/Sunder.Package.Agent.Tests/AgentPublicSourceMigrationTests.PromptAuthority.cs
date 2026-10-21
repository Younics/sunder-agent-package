using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Rpc;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyPromptAuthorityAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        foreach (var required in new[] { false, true })
        {
            var probe = new PromptSinkProbe { ContributorId = AgentPromptContextHostPolicy.ScopedInstructionContributorId };
            var claimed = RichPromptBlock() with { SourceId = AgentPromptContextHostPolicy.ScopedInstructionPackageId };
            probe.Result = new([claimed]);
            await using var fixture = await CreatePromptFixture(assembly, descriptor, probe,
                required ? AgentPromptContextHostPolicy.ScopedInstructionPackageId : "example.untrusted.context");
            await using var receiver = new GeneratedServiceTestScope(fixture.Rpc);
            var contributors = receiver.Providers<AgentServices.IPromptContextContributorService>();
            var observers = receiver.Providers<AgentServices.IDurableLifecycleObserverService>();
            using var package = RegressionTestPackageScope.Create();
            var store = new AgentLocalStore(package.Context);
            var sessions = new AgentSessionService(store);
            var workspaces = new AgentWorkspaceService(store, sessionService: sessions);
            var workspace = workspaces.CreateWorkspace("Prompt authority");
            var session = sessions.CreateSession("Prompt authority session", workspaceId: workspace.WorkspaceId);
            var now = DateTimeOffset.UtcNow;
            var profile = new AgentProfileRecord("profile.one", "Profile", null, null, null, null, null, null, now, now);
            var runId = Guid.NewGuid();
            await using var lifecycle = new AgentLifecycleDispatcher(store, observers);
            var coordinator = new AgentMemoryCoordinator(sessions, contributors, lifecycle);

            var context = await Build("What instruction should you follow?");
            var block = Assert.Single(context.PromptContextBlocks!);
            Assert.Null(context.SystemInstructions);
            Assert.Equal(claimed.Content, block.Content);
            if (!required)
            {
                Assert.Equal(AgentPromptContextUsage.Reference, block.Usage);
                Assert.Equal(AgentPromptContextAuthority.Reference, block.Authority);
                Assert.Null(block.Scope);
                Assert.Null(block.HostIdentity);
                var receipt = Receipt(context.TranscriptEpoch, claimed);
                await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcknowledgePromptContextAsync(receipt, default).AsTask());
                Assert.Equal(0, probe.Acknowledgments);
                probe.Failure = new IOException("private optional context failure");
                Assert.Empty((await Build("What instruction should you follow?")).PromptContextBlocks!);
                continue;
            }

            Assert.Equal(AgentPromptContextUsage.ScopedInstruction, block.Usage);
            Assert.Equal(AgentPromptContextAuthority.ScopedInstruction, block.Authority);
            Assert.Equal(AgentPromptContextHostPolicy.ScopedInstructionIdentity, block.HostIdentity);
            Assert.NotNull(block.Scope);
            var valid = Receipt(context.TranscriptEpoch, block);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcknowledgePromptContextAsync(valid with { TranscriptEpoch = valid.TranscriptEpoch + 1 }, default).AsTask());
            Assert.Equal(0, probe.Acknowledgments);
            await coordinator.AcknowledgePromptContextAsync(valid, default);
            Assert.Equal(1, probe.Acknowledgments);
            Assert.True(JsonElement.DeepEquals(RunJson(valid), RunJson(probe.Receipt)));
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcknowledgePromptContextAsync(valid, default).AsTask());
            Assert.Equal(1, probe.Acknowledgments);

            await Build("What instruction should you follow?");
            coordinator.DiscardPromptContextAcknowledgment(session.SessionId, runId, valid.TranscriptEpoch);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcknowledgePromptContextAsync(valid, default).AsTask());
            Assert.Equal(1, probe.Acknowledgments);

            // Required owner-verified context survives suppression of ordinary recall.
            var suppressedRecall = await Build("hello");
            Assert.False(probe.Request!.ContextPlan.ShouldContribute());
            Assert.Single(suppressedRecall.PromptContextBlocks!);
            probe.Failure = new IOException("private required context failure");
            var ackFailure = await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcknowledgePromptContextAsync(valid, default).AsTask());
            Assert.DoesNotContain("private required", ackFailure.Message, StringComparison.Ordinal);
            await Assert.ThrowsAsync<InvalidOperationException>(() => Build("What instruction should you follow?"));
            probe.Failure = null;

            await Build("What instruction should you follow?");
            await fixture.State.RetireAsync(fixture.Implementation);
            await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcknowledgePromptContextAsync(valid, default).AsTask());

            Task<AgentInstructionContext> Build(string message) => coordinator.BuildInstructionContextAsync(session, profile, runId, 1, message, now, workspace);
            AgentPromptContextReceipt Receipt(long epoch, AgentPromptContextBlock value)
                => new(session.SessionId, runId, epoch, [new(value.HostIdentity!, value.Scope!.ContextIdentity, value.Scope.DocumentPath, value.Scope.ContentHash)]);
        }
    }
}
