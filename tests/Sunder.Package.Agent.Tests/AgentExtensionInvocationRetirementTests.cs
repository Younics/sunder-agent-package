using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Storage;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

extern alias SubagentsWorker;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Models;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using IBehaviorLoopService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Behavior_Loop.IBehaviorLoopService;
using IChildRunExecutorService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Child_Run_Executor.IChildRunExecutorService;
using IRuntimeCatalogService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Runtime_Catalog.IRuntimeCatalogService;
using IToolSourceService = SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Services.CYounics_Ai_Agent_Tool_Source.IToolSourceService;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

public sealed class AgentExtensionInvocationRetirementTests
{
    [Fact]
    public async Task ProviderRetirement_CancelsOnlyExactOwnerAndDrainsAfterSelectionRelease()
    {
        var catalog = new RegressionTestExtensionCatalog();
        var first = new StubChatProvider("provider-a");
        var other = new StubChatProvider("provider-b");
        catalog.AddProvider(RegressionTestBindings.ChatProviders, first, "package.a");
        catalog.AddProvider(RegressionTestBindings.ChatProviders, other, "package.b");
        var references = (await catalog.ChatProviders.ListAsync()).Providers;
        var firstReference = Assert.Single(references, provider => provider.PackageId == "package.a");
        var otherReference = Assert.Single(references, provider => provider.PackageId == "package.b");
        var firstSelection = new AgentRunProviderSelection(null, firstReference, first.Descriptor);
        var otherSelection = new AgentRunProviderSelection(null, otherReference, other.Descriptor);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var otherStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseOther = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstInvocation = InvokeUntilRetiredAsync(
            firstReference,
            CancellationToken.None,
            async token =>
            {
                firstStarted.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return 1;
            });
        var otherInvocation = InvokeUntilRetiredAsync(
            otherReference,
            CancellationToken.None,
            async token =>
            {
                otherStarted.TrySetResult();
                await releaseOther.Task.WaitAsync(token);
                return 2;
            });
        await Task.WhenAll(firstStarted.Task, otherStarted.Task);

        var retirement = catalog.RetireProviderAsync(RegressionTestBindings.ChatProviders, first);
        catalog.AddProvider(RegressionTestBindings.ChatProviders, new StubChatProvider("provider-a"), "package.a");

        await Assert.ThrowsAsync<AgentPackageUnavailableException>(() => firstInvocation);
        Assert.False(otherInvocation.IsCompleted);
        Assert.False(firstSelection.IsAvailable);

        releaseOther.TrySetResult();
        Assert.Equal(2, await otherInvocation);
        await retirement;
    }

    [Fact]
    public async Task ToolRetirement_PreservesDispatchedUncertaintyWithoutFollowingSameIdReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var first = new BlockingToolSource();
        catalog.AddProvider(RegressionTestBindings.ToolSources, first, "package.tools");
        var toolService = CreateToolService(scope.Context, catalog);
        var advertised = Assert.Single(await toolService.ListReadyOwnedRuntimeToolsAsync());
        var execution = toolService.ExecuteAsync(
            advertised.RuntimeTool.Descriptor.ToolId,
            "{}",
            advertisedDescriptor: advertised.RuntimeTool.Descriptor,
            advertisedOwnerPackageId: advertised.OwnerPackageId,
            advertisedInvocation: advertised.Invocation);
        await first.Started.Task;

        var retirement = catalog.RetireProviderAsync(RegressionTestBindings.ToolSources, first);
        var replacement = new BlockingToolSource(block: false);
        catalog.AddProvider(RegressionTestBindings.ToolSources, replacement, "package.tools");

        var result = await execution;
        Assert.True(result.IsError);
        // Started witnessed the original callback, so retirement cannot attest
        // that this operation had no effects or make it safe to replay.
        Assert.Equal(AgentToolResultErrorCodes.ToolOutcomeAmbiguous, result.ErrorCode);
        Assert.Contains("Effects may have occurred", result.Content, StringComparison.Ordinal);
        await retirement;
        Assert.Equal(1, first.ExecutionCount);
        Assert.Equal(0, replacement.ExecutionCount);

        var retriedOldInvocation = await toolService.ExecuteAsync(
            advertised.RuntimeTool.Descriptor.ToolId,
            "{}",
            advertisedDescriptor: advertised.RuntimeTool.Descriptor,
            advertisedOwnerPackageId: advertised.OwnerPackageId,
            advertisedInvocation: advertised.Invocation);
        Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, retriedOldInvocation.ErrorCode);
        Assert.Equal(1, first.ExecutionCount);
        Assert.Equal(0, replacement.ExecutionCount);
    }

    [Fact]
    public async Task PromptContributorRetirement_OmitsOptionalCallbackWithoutInvokingReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var catalog = new RegressionTestExtensionCatalog();
        var contributor = new BlockingPromptContributor("optional");
        catalog.AddProvider(RegressionTestBindings.PromptContextContributors, contributor, "package.context");
        var sessions = new AgentSessionService(new AgentLocalStore(scope.Context), catalog.SessionCleaners);
        var profile = CreateProfile();
        var session = sessions.CreateSession(
            "Session",
            profileId: profile.ProfileId,
            workspaceId: "workspace");
        await using var lifecycle = new AgentLifecycleDispatcher(sessions.Store, catalog.DurableLifecycleObservers);
        var coordinator = new AgentMemoryCoordinator(sessions, catalog.PromptContextContributors, lifecycle);
        var runId = Guid.NewGuid();
        var build = coordinator.BuildInstructionContextAsync(
            session,
            profile,
            runId,
            1,
            "What is the project architecture?",
            DateTimeOffset.UtcNow);
        await contributor.Started.Task;

        var retirement = catalog.RetireProviderAsync(contributor);
        var replacement = new BlockingPromptContributor("optional", block: false);
        catalog.AddProvider(RegressionTestBindings.PromptContextContributors, replacement, "package.context");

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
            RegressionTestBindings.PromptContextContributors,
            contributor,
            AgentPromptContextHostPolicy.ScopedInstructionPackageId);
        var sessions = new AgentSessionService(new AgentLocalStore(scope.Context), catalog.SessionCleaners);
        var profile = CreateProfile();
        var session = sessions.CreateSession(
            "Session",
            profileId: profile.ProfileId,
            workspaceId: "workspace");
        await using var lifecycle = new AgentLifecycleDispatcher(sessions.Store, catalog.DurableLifecycleObservers);
        var coordinator = new AgentMemoryCoordinator(sessions, catalog.PromptContextContributors, lifecycle);
        var runId = Guid.NewGuid();
        var context = await coordinator.BuildInstructionContextAsync(
            session,
            profile,
            runId,
            1,
            "continue",
            DateTimeOffset.UtcNow);

        await catalog.RetireProviderAsync(contributor);
        var replacement = new BlockingPromptContributor("workspace-files", block: false);
        catalog.AddProvider(
            RegressionTestBindings.PromptContextContributors,
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
        catalog.AddChildRunExecutor(executor, "package.child");
        var bindings = catalog.Subagents();
        var runtimeCatalogs = bindings.Providers<IRuntimeCatalogService>();
        var executorReference = await catalog.GetRequiredProviderAsync(
            bindings.Providers<IChildRunExecutorService>());
        var subagentService = new SubagentService(new SubagentStore(scope.Context));
        var permissionAdapter = new SubagentPermissionStatusAdapter(
            bindings.Providers<IToolSourceService>());
        var descriptors = new SubagentDescriptorSchema(
            subagentService,
            permissionAdapter,
            runtimeCatalogs,
            bindings.Providers<IBehaviorLoopService>());
        var renderer = new SubagentBatchResultRenderer(
            subagentService,
            new SubagentRequestParser(),
            permissionAdapter);
        var coordinator = new SubagentChildRunCoordinator(
            runtimeCatalogs,
            bindings.Providers<IChildRunExecutorService>(),
            descriptors,
            permissionAdapter,
            renderer);
        var now = DateTimeOffset.UtcNow;
        var profile = new SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentProfileRecord
        {
            ProfileId = "profile",
            DisplayName = "Profile",
            CreatedAtUtc = new Sunder.Sdk.Rpc.SunderRpcOffsetDateTime(now.ToString("O")),
            UpdatedAtUtc = new Sunder.Sdk.Rpc.SunderRpcOffsetDateTime(now.ToString("O")),
            ModelBindings = [],
            SelectableCapabilityAssignments = [],
            IsInternal = false,
        };
        var workspace = new SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentWorkspaceRecord
        {
            WorkspaceId = "workspace",
            DisplayName = "Workspace",
            CreatedAtUtc = new Sunder.Sdk.Rpc.SunderRpcOffsetDateTime(now.ToString("O")),
            UpdatedAtUtc = new Sunder.Sdk.Rpc.SunderRpcOffsetDateTime(now.ToString("O")),
            Documents = [],
            Paths = [],
        };
        var environment = new SubagentChildRunEnvironment(
            new SubagentsWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentToolExecutionContext
            {
                SessionId = Guid.NewGuid(),
                ProfileId = profile.ProfileId,
                Workspace = workspace,
                RunId = Guid.NewGuid(),
                RunRevision = 1,
                ToolCallId = "call",
                AllowOutsideConfiguredScope = false,
                ApprovedResourceCapabilities = [],
                ApprovedResourceClaims = [],
                ApprovedResourceReferences = [],
                TranscriptEpoch = 1,
            },
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

        var retirement = catalog.RetireProviderAsync(executor);
        var replacement = new BlockingChildRunExecutor(block: false);
        catalog.AddChildRunExecutor(replacement, "package.child");

        var result = await run;
        Assert.True(result.ToolResult.IsError);
        Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, result.ToolResult.ErrorCode);
        await retirement;
        Assert.Equal(0, replacement.InvocationCount);
    }

    [Fact]
    public async Task RetiredProviderReference_RejectsFurtherInvocation()
    {
        using var catalog = new RegressionTestExtensionCatalog();
        var provider = new StubChatProvider("provider");
        catalog.AddProvider(RegressionTestBindings.ChatProviders, provider, "package.provider");
        var reference = Assert.Single((await catalog.ChatProviders.ListAsync()).Providers);

        await catalog.RetireProviderAsync(provider);

        var failure = await Assert.ThrowsAsync<SunderRpcException>(() => reference.EnsureCurrentAsync().AsTask());
        Assert.True(reference.RetirementToken.IsCancellationRequested);
        Assert.True(failure.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable);
    }

    private static AgentToolService CreateToolService(
        IPackageContext packageContext,
        RegressionTestExtensionCatalog catalog)
    {
        var store = new AgentLocalStore(packageContext);
        var sessions = new AgentSessionService(store, catalog.SessionCleaners);
        var workspaces = new AgentWorkspaceService(store, catalog.SessionCleaners, sessions);
        return new AgentToolService(
            sessions,
            workspaces,
            new AgentExecutionTargetService(catalog.ExecutionTargets),
            catalog.ToolSources,
            catalog.ObservedToolExecutions);
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

    private static async Task<T> InvokeUntilRetiredAsync<T>(
        ServiceProvider<AgentServices.IChatProviderService> provider,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task<T>> invoke)
    {
        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            provider.RetirementToken);
        try
        {
            await provider.EnsureCurrentAsync(invocation.Token);
            var result = await invoke(invocation.Token);
            await provider.EnsureCurrentAsync(invocation.Token);
            return result;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested
            && (provider.RetirementToken.IsCancellationRequested
                || exception is SunderRpcException { Error.Kind: SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable }))
        {
            throw new AgentPackageUnavailableException(provider.PackageId, exception);
        }
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
