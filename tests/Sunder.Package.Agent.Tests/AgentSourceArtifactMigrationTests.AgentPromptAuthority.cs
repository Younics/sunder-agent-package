using System.Reflection;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentPromptAuthority(Assembly owner)
    {
        foreach (var required in new[] { false, true })
        {
            using var package = RegressionTestPackageScope.Create();
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
            await using var providerReceiver = new GeneratedServiceTestScope(rpc);
            using var providers = new RegressionTestSourceProviders();
            await using var host = new AgentPromptHost(owner, package.Context, rpc);
            var peer = new AgentPromptPeer { ContributorId = "workspace-files" };
            var registration = providers.Create(RegressionTestBindings.PromptContextContributors, peer, providerReceiver);
            var packageId = required ? "sunder.package.agent.tools.files" : "example.untrusted.prompt";
            var handler = AddSurfaceProvider(state, packageId, registration);
            var built = await host.Build("What instruction should you follow?");
            Assert.Null(built.SystemInstructions);
            var block = Assert.Single(built.Blocks); Assert.Equal(peer.Result!.Blocks[0].Content, block.Content);
            var receipt = host.Receipt(built);
            if (!required)
            {
                Assert.Equal(AgentPromptContextUsage.Reference, block.Usage); Assert.Equal(AgentPromptContextAuthority.Reference, block.Authority);
                Assert.Null(block.Scope); Assert.Null(block.HostIdentity);
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt with { Blocks = [new("claimed.host", new string('b', 64), "/workspace/AGENTS.md", new string('a', 64))] }));
                Assert.Equal(0, peer.Acknowledgments);
                peer.Request = null; Assert.Empty((await host.Build("hello")).Blocks); Assert.Null(peer.Request);
                handler.FailMethod = "contribute"; Assert.Empty((await host.Build("What instruction should you follow?")).Blocks); handler.FailMethod = null;
            }
            else
            {
                Assert.Equal(AgentPromptContextUsage.ScopedInstruction, block.Usage); Assert.Equal(AgentPromptContextAuthority.ScopedInstruction, block.Authority);
                Assert.Equal("sunder.host.scoped-instruction.v1", block.HostIdentity); Assert.NotNull(block.Scope);
                foreach (var mismatch in new[] { receipt with { SessionId = Guid.NewGuid() }, receipt with { RunId = Guid.NewGuid() }, receipt with { TranscriptEpoch = receipt.TranscriptEpoch + 1 } })
                    await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(mismatch));
                Assert.Equal(0, peer.Acknowledgments);
                await host.Acknowledge(receipt); Assert.Equal(1, peer.Acknowledgments);
                Assert.True(JsonElement.DeepEquals(JsonSerializer.SerializeToElement(receipt, MemoryProviderJson), JsonSerializer.SerializeToElement(peer.Receipt, MemoryProviderJson)));
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt));
                await host.Build("What instruction should you follow?"); host.Discard(receipt.TranscriptEpoch);
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt)); Assert.Equal(1, peer.Acknowledgments);
                Assert.Single((await host.Build("hello")).Blocks); Assert.False(peer.Request!.ContextPlan.ShouldContribute());
                handler.FailMethod = "acknowledge";
                var failedAck = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt));
                Assert.DoesNotContain("private-surface", failedAck.Message, StringComparison.Ordinal);
                handler.FailMethod = "contribute";
                var failedBuild = await Assert.ThrowsAsync<InvalidOperationException>(() => host.Build("hello"));
                Assert.DoesNotContain("private-surface", failedBuild.Message, StringComparison.Ordinal); handler.FailMethod = null;

                // Only a valid scope from the owner-verified contributor can retain
                // scoped authority. Self-asserted labels alone are not sufficient.
                peer.Result = new([peer.Result.Blocks[0] with { Scope = null }]);
                var invalidScope = Assert.Single((await host.Build("hello")).Blocks);
                Assert.Equal(AgentPromptContextAuthority.Reference, invalidScope.Authority); Assert.Null(invalidScope.HostIdentity);
                peer.Result = new([AgentPublicSourceMigrationTests.RichPromptBlock()]);
                await host.Build("hello");
                await state.RetireAsync(handler);
                handler = AddSurfaceProvider(state, packageId, registration);
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt));
                Assert.Equal(1, peer.Acknowledgments);
                await host.Build("hello"); await host.Acknowledge(receipt); Assert.Equal(2, peer.Acknowledgments);
                var duplicate = new SurfaceHandler(registration.Handler);
                state.Add(packageId, CurrentPublicRpcDescriptors.Get(registration.ContractId), duplicate, duplicate, Noop.Instance, "fixture.prompt.duplicate", "2.0.0");
                Assert.Equal(2, (await host.Build("hello")).Blocks.Count);
                await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt)); Assert.Equal(2, peer.Acknowledgments);
                await state.RetireAsync(duplicate);
            }
            handler.BlockMethod = "contribute"; handler.Gate = new();
            using var cancelled = new CancellationTokenSource();
            var pending = host.Build("What instruction should you follow?", cancelled.Token);
            await handler.Gate.Started.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending); await handler.Gate.Cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await VerifyAgentWorkspaceDocumentation(owner);
    }

    private static async Task VerifyAgentWorkspaceDocumentation(Assembly owner)
    {
        using var package = RegressionTestPackageScope.Create();
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var now = DateTimeOffset.UtcNow; var path = Path.Combine(package.RootPath, "reference.md");
        await File.WriteAllTextAsync(path, "Untrusted workspace reference 漢字");
        var workspace = new AgentWorkspaceRecord("prompt.documentation", "Documentation", null, now, now,
            Documents: [new("document", "prompt.documentation", path, 0, now, now)]);
        await using var host = new AgentPromptHost(owner, package.Context, rpc, workspace);
        var contributor = AgentRoleBoundary.As<IAgentPromptContextContributor>(host.Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.WorkspaceDocumentationContextService", true)!));
        await using var receiver = new GeneratedServiceTestScope(rpc);
        using var providers = new RegressionTestSourceProviders();
        AddSurfaceProvider(state, "sunder.package.agent", providers.Create(RegressionTestBindings.PromptContextContributors, contributor, receiver));
        var client = Assert.Single((await receiver.Providers<AgentServices.IPromptContextContributorService>().ListAsync()).Providers).Service;
        Assert.Equal(contributor.ContributorId, (await client.DescribeAsync()).ContributorId);
        var built = await host.Build("What repository information do you know?");
        var block = Assert.Single(built.Blocks);
        Assert.Contains("Untrusted workspace reference 漢字", block.Content, StringComparison.Ordinal);
        Assert.Equal(AgentContextProvenance.Tool, block.Provenance); Assert.Equal(AgentContextTrust.Untrusted, block.Trust);
        Assert.Equal(AgentPromptContextAuthority.Reference, block.Authority); Assert.Null(block.Scope); Assert.Null(block.HostIdentity);
        await client.AcknowledgeAsync(host.Receipt(built));
    }

    private sealed class AgentPromptHost : IAsyncDisposable
    {
        internal IServiceProvider Services { get; }
        private readonly IAsyncDisposable _lifetime;
        internal AgentWorkspaceRecord Workspace { get; private set; }
        internal AgentWorkspaceBindingRecord? Binding { get; }
        internal AgentSessionRecord Session { get; }
        internal AgentProfileRecord Profile { get; }
        internal Guid RunId { get; } = Guid.NewGuid();
        internal DateTimeOffset Started { get; } = DateTimeOffset.UtcNow;
        internal IReadOnlyList<AgentToolDescriptor> AvailableTools { get; set; } = [];
        private readonly object _coordinator;
        private readonly object _workspaces;
        private readonly Func<AgentPromptContextReceipt, CancellationToken, ValueTask> _acknowledge;
        private readonly Action<Guid, Guid, long> _discard;

        internal AgentPromptHost(Assembly owner, IPackageContext context, ISunderRpcClient rpc, AgentWorkspaceRecord? workspace = null, string? targetId = null)
        {
            (_, _lifetime, Services) = CreateGeneratedWorker(owner, context, rpc);
            var workspaces = Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentWorkspaceService", true)!);
            _workspaces = workspaces;
            if (workspace is null) Workspace = (AgentWorkspaceRecord)SurfaceCall(workspaces, "CreateWorkspace", "Prompt context")!;
            else
            {
                SurfaceCall(workspaces, "ImportWorkspace", workspace, workspace.Paths, workspace.Documents, targetId);
                Workspace = (AgentWorkspaceRecord)SurfaceCall(workspaces, "GetWorkspace", workspace.WorkspaceId)!;
            }
            Binding = ((IReadOnlyList<AgentWorkspaceBindingRecord>)SurfaceCall(workspaces, "ListBindings", Workspace.WorkspaceId)!).SingleOrDefault();
            Profile = new("profile.prompt", "Prompt profile", null, null, null, null, null, null, Started, Started);
            SurfaceCall(Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentProfileService", true)!), "ImportProfile", Profile);
            var sessions = Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentSessionService", true)!);
            Session = (AgentSessionRecord)SurfaceCall(sessions, "CreateSession", "Prompt session", null, null, null, null, null, null, Profile.ProfileId, null, null, Workspace.WorkspaceId)!;
            _coordinator = Services.GetRequiredService(owner.GetType("Sunder.Package.Agent.Services.AgentMemoryCoordinator", true)!);
            _acknowledge = (receipt, token) => (ValueTask)AgentRoleBoundary.Call(_coordinator, "AcknowledgePromptContextAsync", receipt, token)!;
            _discard = _coordinator.GetType().GetMethod("DiscardPromptContextAcknowledgment", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Action<Guid, Guid, long>>(_coordinator);
        }

        internal async Task<AgentPromptSnapshot> Build(string message, CancellationToken token = default)
        {
            var task = (Task)SurfaceCall(_coordinator, "BuildInstructionContextAsync", Session, Profile, RunId, 1L, message, Started, Workspace, Binding, AvailableTools, token)!;
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            var result = task.GetType().GetProperty("Result")!.GetValue(task)!;
            return new((string?)result.GetType().GetProperty("SystemInstructions")!.GetValue(result),
                AgentRoleBoundary.As<IReadOnlyList<AgentPromptContextBlock>>(result.GetType().GetProperty("PromptContextBlocks")!.GetValue(result)!),
                (long)result.GetType().GetProperty("TranscriptEpoch")!.GetValue(result)!);
        }
        internal AgentPromptContextReceipt Receipt(AgentPromptSnapshot built) => new(Session.SessionId, RunId, built.TranscriptEpoch,
            built.Blocks.Where(block => block.Scope is not null && block.HostIdentity is not null).Select(block => new AgentPromptContextReceiptBlock(
                block.HostIdentity!, block.Scope!.ContextIdentity, block.Scope.DocumentPath, block.Scope.ContentHash)).ToArray());
        internal Task Acknowledge(AgentPromptContextReceipt receipt, CancellationToken token = default) => _acknowledge(receipt, token).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        internal void SetWorkspacePaths(IReadOnlyList<AgentWorkspacePathRecord> paths)
        {
            SurfaceCall(_workspaces, "SaveWorkspacePaths", Workspace.WorkspaceId, paths);
            Workspace = (AgentWorkspaceRecord)SurfaceCall(_workspaces, "GetWorkspace", Workspace.WorkspaceId)!;
        }
        internal void Discard(long epoch) => _discard(Session.SessionId, RunId, epoch);
        public ValueTask DisposeAsync() => _lifetime.DisposeAsync();
    }

    private sealed record AgentPromptSnapshot(string? SystemInstructions, IReadOnlyList<AgentPromptContextBlock> Blocks, long TranscriptEpoch);
}
