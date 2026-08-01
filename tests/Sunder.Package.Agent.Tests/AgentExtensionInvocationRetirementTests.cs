using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Package.Agent.Subagents.Services;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentExtensionInvocationRetirementTests
{
    [Fact]
    public async Task ProviderRetirement_CancelsOnlyExactOwnerAndDrainsAfterSelectionRelease()
    {
        var catalog = new RegressionTestExtensionCatalog();
        var first = new StubChatProvider("provider-a");
        var other = new StubChatProvider("provider-b");
        catalog.AddProvider(AgentRpcServices.ChatProviders, first, "package.a");
        catalog.AddProvider(AgentRpcServices.ChatProviders, other, "package.b");
        var references = catalog.GetServiceReferences(AgentRpcServices.ChatProviders);
        Assert.True(references[0].TryAcquire(out var firstLease));
        Assert.True(references[1].TryAcquire(out var otherLease));
        using var firstSelection = new AgentRunProviderSelection(
            chatBinding: null,
            references[0],
            firstLease,
            first.Descriptor);
        using var otherSelection = new AgentRunProviderSelection(
            chatBinding: null,
            references[1],
            otherLease,
            other.Descriptor);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstInvocation = firstSelection.InvokeAsync(
            CancellationToken.None,
            async (_, token) =>
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            }).AsTask();
        var otherInvocation = otherSelection.InvokeAsync(
            CancellationToken.None,
            async (_, token) =>
            {
                otherStarted.TrySetResult();
                await releaseOther.Task.WaitAsync(token);
                return 2;
            }).AsTask();
        await Task.WhenAll(firstStarted.Task, otherStarted.Task);

        var retirement = catalog.RetireProviderAsync(AgentRpcServices.ChatProviders, first);
        catalog.AddProvider(AgentRpcServices.ChatProviders, new StubChatProvider("provider-a"), "package.a");

        await Assert.ThrowsAsync<AgentPackageUnavailableException>(() => firstInvocation);
        Assert.False(otherInvocation.IsCompleted);
        Assert.False(firstSelection.CanAcquireExactOwner());

        releaseOther.TrySetResult();
        Assert.Equal(2, await otherInvocation);
        firstSelection.Dispose();
        await retirement;
    }

    [Fact]
    public async Task ToolRetirement_DoesNotDispatchPreparedCallToSameIdReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var first = new BlockingToolSource();
        catalog.AddProvider(AgentRpcServices.ToolSources, first, "package.tools");
        var toolService = CreateToolService(scope.Context, catalog);
        var advertised = Assert.Single(await toolService.ListReadyOwnedRuntimeToolsAsync());
        var execution = toolService.ExecuteAsync(
            advertised.RuntimeTool.Descriptor.ToolId,
            "{}",
            advertisedDescriptor: advertised.RuntimeTool.Descriptor,
            advertisedOwnerPackageId: advertised.OwnerPackageId,
            advertisedInvocation: advertised.Invocation);
        await first.Started.Task;

        var retirement = catalog.RetireProviderAsync(AgentRpcServices.ToolSources, first);
        var replacement = new BlockingToolSource(block: false);
        catalog.AddProvider(AgentRpcServices.ToolSources, replacement, "package.tools");

        var result = await execution;
        Assert.True(result.IsError);
        Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, result.ErrorCode);
        await retirement;
        Assert.Equal(0, replacement.ExecutionCount);

        var retriedOldInvocation = await toolService.ExecuteAsync(
            advertised.RuntimeTool.Descriptor.ToolId,
            "{}",
            advertisedDescriptor: advertised.RuntimeTool.Descriptor,
            advertisedOwnerPackageId: advertised.OwnerPackageId,
            advertisedInvocation: advertised.Invocation);
        Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, retriedOldInvocation.ErrorCode);
        Assert.Equal(0, replacement.ExecutionCount);
    }

    [Fact]
    public async Task PromptContributorRetirement_OmitsOptionalCallbackWithoutInvokingReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var contributor = new BlockingPromptContributor("optional");
        catalog.AddProvider(AgentRpcServices.PromptContextContributors, contributor, "package.context");
        var sessions = new AgentSessionService(new AgentLocalStore(scope.Context), catalog);
        var profile = CreateProfile();
        var session = sessions.CreateSession(
            "Session",
            profileId: profile.ProfileId,
            workspaceId: "workspace");
        var coordinator = new AgentMemoryCoordinator(sessions, catalog);
        var runId = Guid.NewGuid();
        var build = coordinator.BuildInstructionContextAsync(
            session,
            profile,
            runId,
            1,
            "What is the project architecture?",
            DateTimeOffset.UtcNow);
        await contributor.Started.Task;

        var retirement = catalog.RetireProviderAsync(
            AgentRpcServices.PromptContextContributors,
            contributor);
        var replacement = new BlockingPromptContributor("optional", block: false);
        catalog.AddProvider(AgentRpcServices.PromptContextContributors, replacement, "package.context");

        await retirement;
        var context = await build;
        Assert.DoesNotContain(context.PromptContextBlocks ?? [], block => block.SourceId == "optional");
        Assert.True(contributor.CancellationObserved.Task.IsCompletedSuccessfully);
        Assert.Equal(0, replacement.ContributionCount);
    }

    [Fact]
    public async Task PromptAcknowledgment_ReacquiresOriginalActivationInsteadOfReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var contributor = new BlockingPromptContributor("workspace-files", block: false);
        catalog.AddProvider(
            AgentRpcServices.PromptContextContributors,
            contributor,
            AgentPromptContextHostPolicy.ScopedInstructionPackageId);
        var sessions = new AgentSessionService(new AgentLocalStore(scope.Context), catalog);
        var profile = CreateProfile();
        var session = sessions.CreateSession(
            "Session",
            profileId: profile.ProfileId,
            workspaceId: "workspace");
        var coordinator = new AgentMemoryCoordinator(sessions, catalog);
        var runId = Guid.NewGuid();
        var context = await coordinator.BuildInstructionContextAsync(
            session,
            profile,
            runId,
            1,
            "continue",
            DateTimeOffset.UtcNow);

        await catalog.RetireProviderAsync(
            AgentRpcServices.PromptContextContributors,
            contributor);
        var replacement = new BlockingPromptContributor("workspace-files", block: false);
        catalog.AddProvider(
            AgentRpcServices.PromptContextContributors,
            replacement,
            AgentPromptContextHostPolicy.ScopedInstructionPackageId);

        await Assert.ThrowsAsync<InvalidOperationException>(() => coordinator.AcknowledgePromptContextAsync(
            new AgentPromptContextReceipt(
                session.SessionId,
                runId,
                context.TranscriptEpoch,
                [new AgentPromptContextReceiptBlock("host", "context", "AGENTS.md", new string('a', 64))]),
            CancellationToken.None).AsTask());
        Assert.Equal(0, replacement.AcknowledgmentCount);
    }

    [Fact]
    public async Task ChildExecutorRetirement_ReturnsPackageUnavailableWithoutInvokingReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var executor = new BlockingChildRunExecutor();
        catalog.AddProvider(AgentRpcServices.ChildRunExecutors, executor, "package.child");
        var executorReference = Assert.Single(catalog.GetServiceReferences(AgentRpcServices.ChildRunExecutors));
        var subagentService = new SubagentService(new SubagentStore(scope.Context));
        var permissionAdapter = new SubagentPermissionStatusAdapter(catalog);
        var descriptors = new SubagentDescriptorSchema(subagentService, catalog, permissionAdapter);
        var renderer = new SubagentBatchResultRenderer(
            subagentService,
            new SubagentRequestParser(),
            permissionAdapter);
        var coordinator = new SubagentChildRunCoordinator(
            catalog,
            descriptors,
            permissionAdapter,
            renderer);
        var profile = CreateProfile();
        var now = DateTimeOffset.UtcNow;
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var environment = new SubagentChildRunEnvironment(
            new AgentToolExecutionContext(
                Guid.NewGuid(),
                profile.ProfileId,
                workspace,
                RunId: Guid.NewGuid(),
                RunRevision: 1,
                ToolCallId: "call"),
            profile,
            executorReference);
        var subagent = new SubagentRecord(
            "child",
            "Child",
            "Does focused work.",
            null,
            null,
            null,
            [],
            now,
            now);
        var run = coordinator.RunAsync(
            environment,
            SubagentConstants.TaskToolId,
            new SubagentTaskRequest("Task", "Do work", subagent.SubagentId),
            subagent,
            CancellationToken.None).AsTask();
        await executor.Started.Task;

        var retirement = catalog.RetireProviderAsync(AgentRpcServices.ChildRunExecutors, executor);
        var replacement = new BlockingChildRunExecutor(block: false);
        catalog.AddProvider(AgentRpcServices.ChildRunExecutors, replacement, "package.child");

        var result = await run;
        Assert.True(result.ToolResult.IsError);
        Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, result.ToolResult.ErrorCode);
        await retirement;
        Assert.Equal(0, replacement.InvocationCount);
    }

    [Fact]
    public void DisposedRegressionLease_RejectsAllMetadataAccess()
    {
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddProvider(AgentRpcServices.ChatProviders, new StubChatProvider("provider"), "package.provider");
        var reference = Assert.Single(catalog.GetServiceReferences(AgentRpcServices.ChatProviders));
        Assert.True(reference.TryAcquire(out var lease));

        lease.Dispose();

        Assert.Throws<ObjectDisposedException>(() => lease.PackageId);
        Assert.Throws<ObjectDisposedException>(() => lease.Service);
        Assert.Throws<ObjectDisposedException>(() => lease.RetirementToken);
    }

    private static AgentToolService CreateToolService(
        IPackageContext packageContext,
        RegressionTestExtensionCatalog catalog)
    {
        var store = new AgentLocalStore(packageContext);
        var sessions = new AgentSessionService(store, catalog);
        var workspaces = new AgentWorkspaceService(store, catalog, sessions);
        return new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(catalog),
            catalog);
    }

    private static AgentProfileRecord CreateProfile()
    {
        var now = DateTimeOffset.UtcNow;
        return new AgentProfileRecord(
            "profile",
            "Profile",
            null,
            null,
            null,
            null,
            null,
            null,
            now,
            now,
            [],
            []);
    }

    private sealed class StubChatProvider(string providerId) : IAgentChatProvider
    {
        public AgentProviderDescriptor Descriptor { get; } = new(
            providerId,
            providerId,
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true)
        {
            PackageId = "package." + providerId,
        };

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>([]);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(string? modelId, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(AgentChatClientContext context, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class BlockingToolSource(bool block = true) : IAgentToolSource
    {
        private static readonly AgentToolDescriptor Tool = new(
            "blocking-tool",
            "Blocking Tool",
            "Blocks until its package retires.",
            IsReadOnly: true);

        public string SourceId => "blocking-source";
        public string DisplayName => "Blocking Source";
        public string SourceKind => "test";
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ExecutionCount { get; private set; }

        public ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentToolDescriptor>>([Tool]);

        public ValueTask<AgentToolReadiness?> GetReadinessAsync(
            string toolId,
            AgentToolSourceContext context,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<AgentToolReadiness?>(
                new AgentToolReadiness(toolId, AgentToolReadinessStatus.Ready, "Ready."));

        public async ValueTask<AgentToolResult> ExecuteAsync(
            AgentToolExecutionContext context,
            AgentToolRequest request,
            CancellationToken cancellationToken = default)
        {
            ExecutionCount++;
            Started.TrySetResult();
            if (block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new AgentToolResult(request.ToolId, "Completed.");
        }
    }

    private sealed class BlockingPromptContributor(string contributorId, bool block = true) :
        IAgentPromptContextContributor,
        IAgentPromptContextAcknowledgmentSink
    {
        public string ContributorId { get; } = contributorId;
        public string DisplayName => ContributorId;
        public string AcknowledgmentSinkId => ContributorId;
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource CancellationObserved { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ContributionCount { get; private set; }
        public int AcknowledgmentCount { get; private set; }

        public async ValueTask<AgentPromptContextContribution?> ContributeContextAsync(
            AgentPromptContextRequest request,
            CancellationToken cancellationToken = default)
        {
            ContributionCount++;
            Started.TrySetResult();
            if (block)
            {
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    CancellationObserved.TrySetResult();
                    throw;
                }
            }
            return null;
        }

        public ValueTask AcknowledgePromptContextAsync(
            AgentPromptContextReceipt receipt,
            CancellationToken cancellationToken = default)
        {
            AcknowledgmentCount++;
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingChildRunExecutor(bool block = true) : IAgentChildRunExecutor
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int InvocationCount { get; private set; }

        public async ValueTask<AgentChildRunResult> RunChildAsync(
            AgentChildRunRequest request,
            CancellationToken cancellationToken = default)
        {
            InvocationCount++;
            Started.TrySetResult();
            if (block)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            return new AgentChildRunResult(
                Guid.NewGuid(),
                AgentRunStatus.Completed,
                "Completed.",
                "Completed.");
        }
    }
}
