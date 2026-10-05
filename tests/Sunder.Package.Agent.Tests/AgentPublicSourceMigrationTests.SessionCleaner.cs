using System.Reflection;
using System.Text.Json;
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
        var baseType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.SessionCleanerService", true)!;
        var historyType = assembly.GetType("Sunder.Package.Agent.Rpc.Public.HistorySessionCleanerProvider", true)!;
        Assert.Equal(baseType, historyType.BaseType);
        Assert.False(baseType.IsInterface);
        Register(baseType, attachments, "agent.attachment.cleaner");
        Register(historyType, history, "agent.history.cleaner");
        var providers = (await rpc.DiscoverAsync(descriptor.ContractId)).Providers;
        Assert.Equal(2, providers.Count);
        await using var scope = await rpc.CreateCallScopeAsync();
        var clientType = assembly.GetType("Sunder.Generated.Rpc.SessionCleaner.SessionCleanerClient", true)!;
        var clients = providers.ToDictionary(provider => provider.ProviderId,
            provider => Activator.CreateInstance(clientType, scope, provider.Endpoint)!, StringComparer.Ordinal);
        foreach (var (id, cleanerId) in new[] { ("agent.attachment.cleaner", attachments.CleanerId), ("agent.history.cleaner", history.CleanerId) })
        {
            var identity = await InvokeGeneratedClient(clients[id], "DescribeAsync", EmptyRequest());
            Assert.Equal(cleanerId, identity.GetProperty("id").GetString());
            Assert.Equal(cleanerId, identity.GetProperty("displayName").GetString());
        }

        services.GetRequiredService<AgentLocalStore>().DeleteSessionTree(removed.SessionId);
        var request = JsonSerializer.SerializeToElement(new { sessionId = removed.SessionId });
        await InvokeGeneratedClient(clients["agent.attachment.cleaner"], "DeleteSessionAsync", request);
        Assert.False(Directory.Exists(removedFolder));
        Assert.True(Directory.Exists(keptFolder));
        Assert.Equal(2, projection.GetSnapshot().DocumentCount);
        await InvokeGeneratedClient(clients["agent.history.cleaner"], "DeleteSessionAsync", request);
        Assert.Equal(1, projection.GetSnapshot().DocumentCount);
        foreach (var client in clients.Values) await InvokeGeneratedClient(client, "DeleteSessionAsync", request);
        Assert.Equal(1, projection.GetSnapshot().DocumentCount);
        Assert.Equal(new byte[] { 4, 5, 6 }, await attachments.ReadAttachmentBytesAsync(keptAttachment.Metadata));
        using (var cancellation = new CancellationTokenSource())
        {
            cancellation.Cancel();
            foreach (var client in clients.Values)
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeGeneratedClient(client, "DeleteSessionAsync",
                    JsonSerializer.SerializeToElement(new { sessionId = kept.SessionId }), cancellation.Token));
            Assert.True(Directory.Exists(keptFolder));
            Assert.Equal(1, projection.GetSnapshot().DocumentCount);
        }

        await VerifyStartedCleanupDrainsAsync(baseType, descriptor, clientType);

        void Register(Type type, IAgentSessionDataCleaner cleaner, string expectedId)
        {
            var implementation = Activator.CreateInstance(type, cleaner)!;
            var registration = Assert.IsType<SunderWorkerProviderRegistration>(type.GetMethod("CreateSunderRpcProviderRegistration",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.DeclaredOnly)!.Invoke(implementation, null));
            Assert.Equal(expectedId, registration.ProviderId);
            Assert.Equal(descriptor.Sha256, registration.DescriptorSha256);
            Assert.Equal(descriptor.AbiSha256, registration.AbiSha256);
            state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        }
    }

    private static async Task VerifyStartedCleanupDrainsAsync(Type serviceType, SunderRpcContractDescriptor descriptor, Type clientType)
    {
        var cleaner = new BlockingSourceCleaner();
        var implementation = Activator.CreateInstance(serviceType, cleaner)!;
        var registration = (SunderWorkerProviderRegistration)serviceType.GetMethod("CreateSunderRpcProviderRegistration")!.Invoke(implementation, null)!;
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        state.Add("sunder.package.agent", descriptor, implementation, registration.Handler, EmptyDisposable.Instance, registration.ProviderId, "2.0.0");
        var provider = Assert.Single((await rpc.DiscoverAsync(descriptor.ContractId)).Providers);
        await using var scope = await rpc.CreateCallScopeAsync();
        var client = Activator.CreateInstance(clientType, scope, provider.Endpoint)!;
        using var cancellation = new CancellationTokenSource();
        var invocation = Task.Run(() => InvokeGeneratedClient(client, "DeleteSessionAsync",
            JsonSerializer.SerializeToElement(new { sessionId = Guid.NewGuid() }), cancellation.Token));
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
        var rejected = await Assert.ThrowsAsync<SunderRpcException>(() => InvokeGeneratedClient(client, "DeleteSessionAsync",
            JsonSerializer.SerializeToElement(new { sessionId = Guid.NewGuid() })));
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
