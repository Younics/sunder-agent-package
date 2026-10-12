using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.HistorySearch;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifySessionCleanersAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        using var package = RegressionTestPackageScope.Create();
        using var catalog = new RegressionTestExtensionCatalog();
        var generated = await CreateCanonicalRuntime(assembly, package.Context, catalog);
        await using var lifetime = generated.Lifetime;
        var services = generated.Services;
        var attachments = services.GetRequiredService<AgentAttachmentService>();
        var history = services.GetRequiredService<HistorySearchIndexingService>();
        var projection = services.GetRequiredService<HistorySearchStore>();
        var sessions = services.GetRequiredService<AgentSessionService>();
        var workspace = services.GetRequiredService<AgentWorkspaceService>().CreateWorkspace("Source cleaner verification");
        var removed = sessions.CreateSession("Removed", workspaceId: workspace.WorkspaceId);
        var kept = sessions.CreateSession("Kept", workspaceId: workspace.WorkspaceId);
        sessions.AppendTextTurn(removed.SessionId, AgentMessageRole.User, "removed history canary");
        sessions.AppendTextTurn(kept.SessionId, AgentMessageRole.User, "kept history canary");
        await history.StartAsync();
        try
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (projection.GetSnapshot().DocumentCount != 2 && DateTime.UtcNow < deadline) await Task.Delay(20);
            Assert.Equal(2, projection.GetSnapshot().DocumentCount);
        }
        finally { await history.StopAsync(); }
        await attachments.StoreAttachmentAsync(removed.SessionId, new("remove.txt", "text/plain", [1, 2, 3]));
        var keptAttachment = await attachments.StoreAttachmentAsync(kept.SessionId, new("keep.txt", "text/plain", [4, 5, 6]));
        var attachmentRoot = package.Context.Storage.RoleLocalWorkspace.GetLocalPath("agent/attachments");
        var removedFolder = Path.Combine(attachmentRoot, removed.SessionId.ToString("N"));
        var keptFolder = Path.Combine(attachmentRoot, kept.SessionId.ToString("N"));
        Assert.True(Directory.Exists(removedFolder));
        Assert.True(Directory.Exists(keptFolder));

        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        var baseType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.SessionCleanerService", true)!;
        Assert.True(baseType.IsInterface);
        Assert.Null(assembly.GetType("Sunder.Package.Agent.Rpc.Public.HistorySessionCleanerProvider"));
        Register(attachments, "agent.attachment.cleaner");
        Register(history, "agent.history.cleaner");
        var providers = (await receiver.Providers<AgentServices.ISessionCleanerService>().ListAsync()).Providers;
        Assert.Equal(2, providers.Count);
        var clients = providers.ToDictionary(provider => provider.ProviderId, provider => provider.Service, StringComparer.Ordinal);
        foreach (var (id, cleanerId) in new[] { ("agent.attachment.cleaner", attachments.CleanerId), ("agent.history.cleaner", history.CleanerId) })
        {
            var identity = await clients[id].DescribeAsync();
            Assert.Equal(cleanerId, identity.Id);
            Assert.Equal(cleanerId, identity.DisplayName);
        }

        services.GetRequiredService<AgentLocalStore>().DeleteSessionTree(removed.SessionId);
        await clients["agent.attachment.cleaner"].DeleteSessionAsync(removed.SessionId);
        Assert.False(Directory.Exists(removedFolder));
        Assert.True(Directory.Exists(keptFolder));
        Assert.Equal(2, projection.GetSnapshot().DocumentCount);
        await clients["agent.history.cleaner"].DeleteSessionAsync(removed.SessionId);
        Assert.Equal(1, projection.GetSnapshot().DocumentCount);
        foreach (var client in clients.Values) await client.DeleteSessionAsync(removed.SessionId);
        Assert.Equal(1, projection.GetSnapshot().DocumentCount);
        Assert.Equal(new byte[] { 4, 5, 6 }, await attachments.ReadAttachmentBytesAsync(keptAttachment.Metadata));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            foreach (var client in clients.Values)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DeleteSessionAsync(kept.SessionId, cancellation.Token).AsTask());
            Assert.True(Directory.Exists(keptFolder));
            Assert.Equal(1, projection.GetSnapshot().DocumentCount);
        }

        await VerifyStartedCleanupDrainsAsync(descriptor);

        void Register(IAgentSessionDataCleaner cleaner, string expectedId)
        {
            var implementation = AgentRoleBoundary.Unwrap(cleaner);
            using var sources = new RegressionTestSourceProviders();
            var registration = sources.Create(descriptor.ContractId, implementation, receiver);
            Assert.Equal(expectedId, registration.ProviderId);
            Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
            Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
            state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        }
    }

    private static async Task VerifyStartedCleanupDrainsAsync(SunderRpcContractDescriptor descriptor)
    {
        var cleaner = new BlockingSourceCleaner();
        var implementation = cleaner;
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        await using var receiver = new GeneratedServiceTestScope(rpc);
        using var sources = new RegressionTestSourceProviders();
        var registration = sources.Create(descriptor.ContractId, implementation, receiver);
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var client = Assert.Single((await receiver.Providers<AgentServices.ISessionCleanerService>().ListAsync()).Providers).Service;
        using var cancellation = new CancellationTokenSource();
        var invocation = Task.Run(() => client.DeleteSessionAsync(Guid.NewGuid(), cancellation.Token).AsTask());
        try
        {
            await cleaner.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.False(invocation.IsCompleted);
            Assert.False(cleaner.Completed);
        }
        finally { cleaner.Release.TrySetResult(); }
        var failure = await Record.ExceptionAsync(async () => await invocation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(failure is null or OperationCanceledException, failure?.ToString());
        Assert.True(cleaner.Completed);
        cleaner.Fail = true;
        var rejected = await Assert.ThrowsAsync<SunderRpcException>(() => client.DeleteSessionAsync(Guid.NewGuid()).AsTask());
        Assert.Equal(SunderRpcErrorKind.ProviderFaulted, rejected.Error.Kind);
        Assert.DoesNotContain("private cleanup backend detail", rejected.Message, StringComparison.Ordinal);
    }

    private sealed class BlockingSourceCleaner : IAgentSessionDataCleaner
    {
        public string CleanerId => "test.blocking.cleaner";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Completed { get; private set; }
        public bool Fail { get; set; }
        public void DeleteSessionData(Guid sessionId)
        {
            if (Fail) throw new IOException("private cleanup backend detail");
            Started.TrySetResult();
            Release.Task.GetAwaiter().GetResult();
            Completed = true;
        }
    }
}
