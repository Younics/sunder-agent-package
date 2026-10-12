using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyAgentFilesPromptContext(Assembly owner, Assembly files, Assembly local)
    {
        using var package = RegressionTestPackageScope.Create();
        var root = OperatingSystem.IsMacOS() && package.RootPath.StartsWith("/var/", StringComparison.Ordinal) ? "/private" + package.RootPath : package.RootPath;
        var filesContext = new TestPackageContext("sunder.package.agent.tools.files", root, version: "2.0.0", storage: package.Context.Storage);
        var localContext = new TestPackageContext("sunder.package.agent.execution.local", root, version: "2.0.0", storage: package.Context.Storage);
        using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
        var (localOptions, localLifetime, _) = CreateGeneratedWorker(local, localContext, new RegressionTestRpcClient(state, localContext.PackageId));
        await using var ownedLocal = localLifetime;
        var target = Assert.Single(localOptions.Providers, value => value.ProviderId == "local.execution.target");
        state.Add(localContext.PackageId, CurrentPublicRpcDescriptors.Get(target.ContractId), target.Handler, target.Handler, Noop.Instance, target.ProviderId, "2.0.0");
        var rpc = new RegressionTestRpcClient(state, "sunder.package.agent");
        var targetReceiver = new GeneratedServiceTestScope(rpc);
        var filesRpc = new FilesRpcObserver(new RegressionTestRpcClient(state, filesContext.PackageId));
        var (options, filesLifetime, _) = CreateGeneratedWorker(files, filesContext, filesRpc);
        await using var ownedFiles = filesLifetime;
        var handlers = options.Providers.ToDictionary(value => value.ProviderId, value => AddSurfaceProvider(state, filesContext.PackageId, value));
        var workspaceRoot = Directory.CreateDirectory(Path.Combine(root, "prompt workspace")).FullName;
        var document = Path.Combine(workspaceRoot, "AGENTS.md");
        await File.WriteAllTextAsync(document, "Use scoped fixture instructions.");
        await File.WriteAllTextAsync(Path.Combine(workspaceRoot, "data.txt"), "protected fixture content");
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("prompt.files", "Files prompt", null, now, now,
            [new("path", "prompt.files", workspaceRoot, true, 0, now, now)]);
        await using var host = new AgentPromptHost(owner, package.Context, rpc, workspace, "local");
        Assert.NotNull(host.Binding);
        var targetContext = new AgentExecutionTargetContext(host.Session.SessionId, host.Profile.ProfileId, host.Workspace, host.Binding);
        var targetProvider = Assert.Single((await targetReceiver.Providers<AgentServices.IExecutionTargetService>().ListAsync()).Providers);
        var targetReference = targetProvider.Reference;
        var generation = await targetProvider.Service.ConfigurationGenerationAsync(targetContext);
        await options.OnActivated!(null!, default);
        var toolClient = Assert.Single((await targetReceiver.Providers<AgentServices.IToolSourceService>().ListAsync()).Providers).Service;
        host.AvailableTools = await toolClient.ListToolsAsync(new(host.Session.SessionId, host.Profile, host.Workspace, host.Binding)
        { ExecutionTarget = targetReference, ExecutionTargetConfigurationGeneration = generation });
        var built = await host.Build("hello"); // Required scope collection is independent of optional memory recall.
        var receipt = host.Receipt(built); Assert.NotEmpty(receipt.Blocks); Assert.True(receipt.TranscriptEpoch > 0);
        Assert.Contains(built.Blocks, block => block.Content.Contains("Use scoped fixture instructions.", StringComparison.Ordinal));
        var scoped = built.Blocks.Where(block => block.Scope is not null).ToArray();
        Assert.Equal(receipt.Blocks.Count, scoped.Length); Assert.NotEmpty(scoped);
        Assert.All(scoped, block =>
        {
            Assert.Equal(AgentPromptContextUsage.ScopedInstruction, block.Usage);
            Assert.Equal(AgentPromptContextAuthority.ScopedInstruction, block.Authority);
            Assert.Equal("sunder.host.scoped-instruction.v1", block.HostIdentity);
            Assert.Equal(AgentContextTrust.Untrusted, block.Trust);
        });
        var reference = Assert.Single(built.Blocks, block => block.Scope is null);
        Assert.Equal(AgentPromptContextUsage.Reference, reference.Usage);
        Assert.Equal(AgentPromptContextAuthority.Reference, reference.Authority); Assert.Null(reference.HostIdentity);
        Assert.Equal(AgentContextTrust.Untrusted, reference.Trust);
        var sent = handlers["files.prompt.context"].Calls.Last(call => call.Method == "contribute").Request;
        Assert.False(sent.GetProperty("contextPlan").Deserialize<AgentPromptContextPlan>(MemoryProviderJson)!.ShouldContribute());
        var targetSnapshot = (SunderRpcProviderSnapshot)targetReference.GetType()
            .GetProperty("Snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(targetReference)!;
        var expectedLocator = JsonSerializer.SerializeToElement(new
        {
            targetSnapshot.PackageId, targetSnapshot.PackageVersion, targetSnapshot.ProviderId,
            targetSnapshot.ContractId, targetSnapshot.ContractVersion, targetSnapshot.DescriptorSha256,
            targetSnapshot.AbiSha256, targetSnapshot.ActivationId, targetSnapshot.ActivationEpoch,
            targetSnapshot.SessionGeneration, endpoint = targetSnapshot.Endpoint.Value,
        }, MemoryProviderJson);
        Assert.Equal(11, sent.GetProperty("executionTarget").EnumerateObject().Count());
        Assert.True(JsonElement.DeepEquals(expectedLocator, sent.GetProperty("executionTarget")));
        var withheld = await Read(); Assert.Equal("files-prompt-context-refresh-required", withheld.ErrorCode);
        Assert.DoesNotContain("protected fixture content", withheld.Content ?? "", StringComparison.Ordinal);
        var invalid = receipt with { Blocks = [receipt.Blocks[0] with { ContentHash = new string('e', 64) }] };
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(invalid));
        Assert.Equal("files-prompt-context-refresh-required", (await Read()).ErrorCode);
        built = await host.Build("hello"); receipt = host.Receipt(built); await host.Acknowledge(receipt);
        var read = await Read(); Assert.False(read.IsError, read.Content); Assert.Contains("protected fixture content", read.Content!, StringComparison.Ordinal);
        var classified = await toolClient.PermissionAsync(Execution(), new("read", "{\"path\":\"data.txt\"}"));
        Assert.Equal("files.read", classified!.ActionId); Assert.Equal(AgentPermissionBoundaryIds.ConfiguredScope, classified.BoundaryId);
        Assert.Null(await toolClient.PreflightAsync(Execution(), new("read", "{\"path\":\"data.txt\"}")));
        Assert.NotNull(await toolClient.PresentationAsync(new("read", "{\"path\":\"data.txt\"}", read.Summary,
            read.Content, null, null, read.IsError, read.ErrorCode, read.BackendId)));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt));

        await File.WriteAllTextAsync(document, "Changed scoped fixture instructions.");
        Assert.Equal("files-prompt-context-refresh-required", (await Read()).ErrorCode);
        built = await host.Build("hello"); var changed = host.Receipt(built);
        Assert.NotEqual(receipt.Blocks[0].ContentHash, changed.Blocks[0].ContentHash);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(receipt));
        built = await host.Build("hello"); changed = host.Receipt(built); await host.Acknowledge(changed);
        Assert.False((await Read()).IsError);

        // A workspace-scope change produces a new receipt binding. An earlier
        // document hash is insufficient to acknowledge the new execution scope.
        var secondRoot = Directory.CreateDirectory(Path.Combine(root, "second scope")).FullName;
        host.SetWorkspacePaths([.. host.Workspace.Paths, new("second", host.Workspace.WorkspaceId, secondRoot, false, 1, now, now)]);
        targetContext = targetContext with { Workspace = host.Workspace };
        generation = await targetProvider.Service.ConfigurationGenerationAsync(targetContext);
        built = await host.Build("hello"); var reconfigured = host.Receipt(built);
        Assert.NotEqual(changed.Blocks[0].ContextIdentity, reconfigured.Blocks[0].ContextIdentity);
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(changed));
        built = await host.Build("hello"); reconfigured = host.Receipt(built); await host.Acknowledge(reconfigured);
        Assert.False((await Read()).IsError);

        built = await host.Build("hello"); var pendingReceipt = host.Receipt(built);
        var old = handlers["files.prompt.context"];
        await state.RetireAsync(old);
        var replacement = AddSurfaceProvider(state, filesContext.PackageId, Assert.Single(options.Providers, value => value.ProviderId == "files.prompt.context"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => host.Acknowledge(pendingReceipt));
        Assert.Empty(replacement.Calls);
        built = await host.Build("hello"); await host.Acknowledge(host.Receipt(built));
        Assert.Single(replacement.Calls, call => call.Method == "acknowledge");
        await targetReceiver.DisposeAsync();
        await filesLifetime.DisposeAsync();
        Assert.Equal(0, filesRpc.ActiveWatches); filesRpc.AssertScopedOperations(); Assert.All(filesRpc.Scopes, scope => Assert.True(scope.Disposed));

        AgentToolExecutionContext Execution() => new(host.Session.SessionId, host.Profile.ProfileId, host.Workspace, host.Binding,
                RunId: host.RunId, RunRevision: 1, ToolCallId: "prompt.read")
            { TranscriptEpoch = built.TranscriptEpoch, ExecutionTargetConfigurationGeneration = generation, ExecutionTarget = targetReference };
        Task<AgentToolResult> Read() => toolClient.ExecuteAsync(Execution(), new("read", "{\"path\":\"data.txt\"}")).AsTask();
    }
}
