extern alias AgentCore;
extern alias LocalWorker;
extern alias McpWorker;
extern alias MemoryWorker;
extern alias AnthropicWorker;
extern alias GeminiWorker;
extern alias SkillsWorker;
extern alias SkillsApp;
extern alias SubagentsWorker;
extern alias SubagentsApp;
extern alias FilesWorker;
extern alias ShellWorker;

using System.Collections.Specialized;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Data.Sqlite;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.HistorySearch;
using McpWorker::Sunder.Package.Agent.Mcp;
using McpWorker::Sunder.Package.Agent.Mcp.Runtime;
using McpWorker::Sunder.Package.Agent.Mcp.Services;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic;
using MemoryWorker::Sunder.Package.Agent.Memory.Semantic.Services;
using Sunder.Package.Agent.Models;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AnthropicWorker::Sunder.Package.Agent.Provider.Anthropic;
using GeminiWorker::Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Services.BehaviorLoops;
using SkillsApp::Sunder.Package.Agent.Skills.PackageViews;
using SkillsWorker::Sunder.Package.Agent.Skills.Services;
using Sunder.Package.Agent.Storage;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Models;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using SubagentsWorker::Sunder.Package.Agent.Subagents.Services;
using FilesWorker::Sunder.Package.Agent.Tools.Files;
using ShellWorker::Sunder.Package.Agent.Tools.Shell;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentPresentationDispatcher = AgentCore::Sunder.Package.Agent.Shared.Presentation.IPresentationDispatcher;
using AgentChatSelectionStateService = AgentCore::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using AgentToolPresentationService = AgentCore::Sunder.Package.Agent.Services.AgentToolPresentationService;
using AppRuntime = AgentCore::Sunder.Package.Agent.Runtime;
using AppModels = AgentCore::Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Tests;

extern alias McpApp;
using AgentMcpSettingsViewModel = McpApp::Sunder.Package.Agent.Mcp.AgentMcpSettingsViewModel;
using McpStatusKind = McpApp::Sunder.Package.Agent.Mcp.McpStatusKind;

public sealed partial class AgentRunCoordinatorTests
{

    [Fact]
    public async Task QueueUserMessageAsync_RunsParallelSafeToolCallsConcurrently()
    {
        const string firstToolId = "first_parallel_tool";
        const string secondToolId = "second_parallel_tool";

        var provider = new ScriptedProvider(
            (request, requestIndex) =>
                requestIndex switch
                {
                    1 =>
                    [
                        ToolRequests(
                            new AgentToolCallRequest("call-1", firstToolId, "{\"value\":1}"),
                            new AgentToolCallRequest("call-2", secondToolId, "{\"value\":2}")
                        ),
                    ],
                    2 => [AssertAndComplete(request, firstToolId, "call-1")],
                    _ => throw new Xunit.Sdk.XunitException(
                        $"Unexpected provider request {requestIndex}."
                    ),
                },
            supportsMultipleToolCalls: true
        );
        var tracker = new ConcurrentToolExecutionTracker();
        var firstTool = new ConcurrentTrackingTool(firstToolId, tracker, AgentToolConcurrencyMode.ParallelSafe);
        var secondTool = new ConcurrentTrackingTool(secondToolId, tracker, AgentToolConcurrencyMode.ParallelSafe);
        using var runtime = AgentTestRuntime.Create(provider, firstTool, secondTool);
        var sessionId = await runtime.CreateSessionAsync(firstToolId);
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            profile.EmbeddingProviderId,
            profile.EmbeddingModelId,
            selectableCapabilityAssignments:
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Tool,
                    firstToolId
                ),
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Tool,
                    secondToolId
                ),
            ]
        );

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use both tools.",
            runtime.CurrentWorkspaceId
        );

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(1, firstTool.ExecutionCount);
        Assert.Equal(1, secondTool.ExecutionCount);
        Assert.True(tracker.MaxConcurrentExecutions >= 2);
        Assert.Contains(
            provider.Requests[1].Turns,
            turn =>
                turn.Kind == AgentTurnKind.ToolResult
                && turn.Items.Any(item => item.ToolId == secondToolId && item.CallId == "call-2")
        );
    }

    [Fact]
    public async Task QueueUserMessageAsync_PersistsCompletedParallelOutcomeBeforeChildSuspension()
    {
        const string waitingToolId = "waiting_child_tool";
        const string completedToolId = "completed_parallel_tool";
        var provider = new ScriptedProvider(
            (_, requestIndex) =>
                requestIndex == 1
                    ? [ToolRequests(
                        new AgentToolCallRequest("waiting-call", waitingToolId, "{}"),
                        new AgentToolCallRequest("completed-call", completedToolId, "{}"))]
                    : throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            supportsMultipleToolCalls: true);
        var waitingTool = new WaitingChildTool(waitingToolId);
        var completedTool = new TestTool(
            completedToolId,
            concurrencyMode: AgentToolConcurrencyMode.ParallelSafe);
        using var runtime = AgentTestRuntime.Create(provider, waitingTool, completedTool);
        var sessionId = await runtime.CreateSessionAsync(waitingToolId);
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            profile.EmbeddingProviderId,
            profile.EmbeddingModelId,
            selectableCapabilityAssignments:
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Tool,
                    waitingToolId),
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Tool,
                    completedToolId),
            ]);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run both parallel tools.",
            runtime.CurrentWorkspaceId);

        Assert.True(
            checkpoint.Status == AgentRunStatus.WaitingForApproval,
            checkpoint.Summary ?? checkpoint.Status.ToString());
        Assert.Equal(1, waitingTool.ExecutionCount);
        Assert.Equal(1, completedTool.ExecutionCount);
        Assert.Contains(
            runtime.SessionService.ListTurns(sessionId),
            turn => turn.Kind == AgentTurnKind.ToolResult
                    && turn.Items.Any(item => item.CallId == "completed-call"));
        Assert.Equal(
            AgentRunStatus.WaitingForApproval,
            runtime.SessionService.GetLatestCheckpoint(sessionId)?.Status);
    }

    [Fact]
    public async Task ToolBatch_PreservesPositionAndPairsCallsAfterPermissionSuspension()
    {
        const string firstToolId = "batch_first";
        const string permissionToolId = "batch_permission";
        const string canceledToolId = "batch_canceled";
        var provider = new ScriptedProvider(
            (_, requestIndex) => requestIndex == 1
                ? [ToolRequests(
                    new AgentToolCallRequest("call-1", firstToolId, "{}"),
                    new AgentToolCallRequest("call-2", permissionToolId, "{}"),
                    new AgentToolCallRequest("call-3", canceledToolId, "{}"))]
                : throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            supportsMultipleToolCalls: true);
        var firstTool = new TestTool(
            firstToolId,
            concurrencyMode: AgentToolConcurrencyMode.ParallelSafe);
        var canceledTool = new TestTool(canceledToolId);
        using var runtime = AgentTestRuntime.Create(provider, firstTool, canceledTool);
        var permissionTool = new PermissionedToolSource(permissionToolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, permissionTool);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, permissionTool);
        var sessionId = await runtime.CreateSessionAsync(firstToolId);
        var profile = runtime.CurrentProfile;
        runtime.ProfileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            profile.ChatProviderId,
            profile.ChatModelId,
            profile.EmbeddingProviderId,
            profile.EmbeddingModelId,
            selectableCapabilityAssignments:
            [
                new(AgentProfileSelectableCapabilityKinds.Tool, firstToolId),
                new(AgentProfileSelectableCapabilityKinds.Tool, permissionToolId),
                new(AgentProfileSelectableCapabilityKinds.Tool, canceledToolId),
            ]);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the batch.",
            runtime.CurrentWorkspaceId);

        Assert.True(
            checkpoint.Status == AgentRunStatus.WaitingForApproval,
            checkpoint.Summary ?? checkpoint.Status.ToString());
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));
        Assert.Equal("call-2", pending.CallId);
        Assert.NotNull(pending.ToolExecutionId);
        Assert.Equal(
            AgentToolExecutionStatus.Prepared,
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value)?.Status);
        Assert.Equal(1, firstTool.ExecutionCount);
        Assert.Equal(0, permissionTool.ExecutionCount);
        Assert.Equal(0, canceledTool.ExecutionCount);
        var items = runtime.SessionService.ListTurns(sessionId).SelectMany(turn => turn.Items).ToArray();
        Assert.Contains(items, item => item.CallId == "call-1" && item.Kind == AgentTurnItemKind.ToolResult);
        Assert.Contains(items, item => item.CallId == "call-2" && item.Kind == AgentTurnItemKind.ToolCall);
        Assert.Contains(items, item => item.CallId == "call-3"
                                      && item.Kind == AgentTurnItemKind.ToolResult
                                      && item.ErrorCode == "tool-batch-canceled");
    }

    [Fact]
    public async Task ApprovePendingPermissionAsync_ContinuesProviderAfterApprovedToolResult()
    {
        const string toolId = "approval_tool";

        var provider = new ScriptedProvider(
            (request, requestIndex) =>
                requestIndex switch
                {
                    1 => ToolRequest("call-1", toolId, "{\"path\":\"~\"}"),
                    2 => AssertAndComplete(request, toolId, "call-1"),
                    _ => throw new Xunit.Sdk.XunitException(
                        $"Unexpected provider request {requestIndex}."
                    ),
                }
        );
        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.PermissionSurfaces,
            toolSource
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var observedPendingRequest = false;
        runtime.SessionService.SessionChanged += changedSessionId =>
        {
            if (changedSessionId == sessionId
                && runtime.PermissionService.ListPendingRequests(sessionId).Count == 1
                && runtime.SessionService.GetLatestCheckpoint(sessionId)?.Status == AgentRunStatus.WaitingForApproval)
            {
                observedPendingRequest = true;
            }
        };

        var waitingCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.",
            runtime.CurrentWorkspaceId
        );

        Assert.Equal(AgentRunStatus.WaitingForApproval, waitingCheckpoint.Status);
        Assert.True(observedPendingRequest);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));
        var preparedExecution = Assert.IsType<AgentToolExecutionRecord>(
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value));
        Assert.Equal("test.package", preparedExecution.OwnerPackageId);
        var suspendedRun = Assert.IsType<AgentDurableRunRecord>(runtime.Store.GetRun(pending.RunId));
        Assert.Equal(AgentDurableRunStatus.WaitingForApproval, suspendedRun.Status);
        Assert.IsType<AgentPermissionRunSuspension>(suspendedRun.Suspension);
        Assert.Contains(
            runtime.SessionService.ListTurns(sessionId),
            turn => turn.Kind == AgentTurnKind.ToolCall
                    && turn.Items.Any(item => item.CallId == pending.CallId));

        var completedCheckpoint = await runtime.RunCoordinator.ApprovePendingPermissionAsync(
            sessionId,
            pending.RequestId
        );

        Assert.NotNull(completedCheckpoint);
        Assert.Equal(AgentRunStatus.Completed, completedCheckpoint!.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(1, toolSource.ExecutionCount);
        Assert.All(
            runtime.SessionService.ListTurns(sessionId)
                .SelectMany(static turn => turn.Items)
                .Where(item => item.ToolExecutionId == preparedExecution.ExecutionId),
            item => Assert.Equal("test.package", item.ToolOwnerPackageId));
        Assert.Contains(
            runtime.SessionService.ListTurns(sessionId),
            turn =>
                turn.Role == AgentMessageRole.Assistant
                && turn.Kind == AgentTurnKind.Message
                && RenderTurnText(turn).Contains("Used the tool result", StringComparison.Ordinal)
        );
    }

    [Fact]
    public async Task SessionChanged_CanApproveNewPendingRequestBeforeQueueCallReturns()
    {
        const string toolId = "approval_tool";
        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{\"path\":\"~\"}"),
            2 => AssertAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        Task<AgentRunCheckpointRecord?>? approvalTask = null;
        var approvalStarted = 0;
        var activeRunWasCleared = false;
        runtime.SessionService.SessionChanged += changedSessionId =>
        {
            var pending = changedSessionId == sessionId
                ? runtime.PermissionService.ListPendingRequests(sessionId).SingleOrDefault()
                : null;
            if (pending is not null
                && Interlocked.CompareExchange(ref approvalStarted, 1, 0) == 0)
            {
                activeRunWasCleared = runtime.ActiveRunRegistry.GetCurrent(
                    sessionId,
                    pending.RunId,
                    pending.RunRevision) is null;
                approvalTask = runtime.RunCoordinator.ApprovePendingPermissionAsync(
                    sessionId,
                    pending.RequestId);
            }
        };

        var waitingCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.",
            runtime.CurrentWorkspaceId);
        var completedCheckpoint = await Assert.IsType<Task<AgentRunCheckpointRecord?>>(approvalTask);

        Assert.Equal(AgentRunStatus.WaitingForApproval, waitingCheckpoint.Status);
        Assert.True(activeRunWasCleared);
        Assert.Equal(AgentRunStatus.Completed, completedCheckpoint?.Status);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(sessionId));
        Assert.Equal(1, toolSource.ExecutionCount);
    }

    [Fact]
    public async Task ApprovePendingPermission_RespectsNewConfiguredDeny()
    {
        const string toolId = "approval_tool";
        var provider = new ScriptedProvider((_, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var waiting = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.",
            runtime.CurrentWorkspaceId);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));
        runtime.PermissionService.SaveOverride(
            pending.ActionId,
            pending.BoundaryId,
            AgentPermissionDecision.Deny);

        var denied = await runtime.RunCoordinator.ApprovePendingPermissionAsync(
            sessionId,
            pending.RequestId);

        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.Equal(AgentRunStatus.Stopped, denied?.Status);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(sessionId));
        Assert.Equal(0, toolSource.ExecutionCount);
        Assert.Contains(
            runtime.SessionService.ListTurns(sessionId).SelectMany(static turn => turn.Items),
            item => item.Kind == AgentTurnItemKind.ToolResult
                    && item.CallId == pending.CallId
                    && item.ErrorCode == "permission-denied");
    }

    [Fact]
    public async Task PendingPermissionPersistenceFailure_ReleasesPreparedResourceAuthority()
    {
        const string toolId = "persistence_failure_authority_tool";
        var provider = new ScriptedProvider((_, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new PreflightPermissionedToolSource(toolId, deferInPreflight: false);
        var target = new ReleasingResourceAuthorityExecutionTarget();
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, source);
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.ExecutionTargets,
            target,
            "test.execution.target.package");
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.WorkspaceService.SavePrimaryExecutionBinding(
            runtime.CurrentWorkspaceId,
            target.Descriptor.TargetId);
        using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = runtime.Store.DatabasePath,
            Pooling = false,
        }.ToString()))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TRIGGER FailPendingPermissionInsert
                BEFORE INSERT ON AgentPendingPermissionRequests
                BEGIN
                    SELECT RAISE(ABORT, 'injected pending-permission persistence failure');
                END;
                """;
            command.ExecuteNonQuery();
        }

        try
        {
            await runtime.RunCoordinator.QueueUserMessageAsync(
                sessionId,
                runtime.CurrentProfileId,
                "Trigger permission persistence failure.",
                runtime.CurrentWorkspaceId);
        }
        catch (Exception ex) when (ex is SqliteException or InvalidOperationException)
        {
        }

        Assert.Equal(["test-transient-outside-authority"], target.ReleasedCapabilities);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(sessionId));
    }

    [Fact]
    public async Task ToolPreflight_DefersBeforeLedgerStartAndForcesPromptContextRefresh()
    {
        const string toolId = "preflight_deferred_tool";
        const string errorCode = "test-preflight-deferred";
        var provider = new ScriptedProvider(
            (request, requestIndex) => requestIndex switch
            {
                1 => ToolRequest("call-1", toolId, "{}"),
                2 => AssertErroredToolResultAndComplete(
                    request,
                    toolId,
                    "call-1",
                    errorCode,
                    "No mutation was dispatched."),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new PreflightPermissionedToolSource(
            toolId,
            deferInPreflight: true,
            deferredErrorCode: errorCode);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PromptContextContributors, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.PermissionService.SetSessionUnrestrictedMode(sessionId, isEnabled: true);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the preflight tool and refresh its instruction context.",
            runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(1, source.PreflightCount);
        Assert.Equal(0, source.ExecutionCount);
        Assert.True(source.PreflightAllowedOutsideConfiguredScope);
        Assert.Single(source.PreflightApprovedResourceClaims);
        Assert.Equal(["test-transient-outside-authority"], source.PreflightApprovedResourceCapabilities);
        Assert.True(source.ContextContributionCount >= 2);
        var execution = Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status);
        Assert.Null(execution.StartedAtUtc);
        Assert.Equal(errorCode, execution.OutcomeCode);
    }

    [Fact]
    public async Task DispatchAwareTool_PreDispatchFailureNeverMarksLedgerStarted()
    {
        const string toolId = "dispatch_aware_failure";
        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            2 => AssertErroredToolResultAndComplete(
                request,
                toolId,
                "call-1",
                "test-pre-dispatch-failure",
                "Connection failed before dispatch."),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new DispatchAwareToolSource(toolId, DispatchAwareToolBehavior.FailBeforeDispatch);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ObservedToolExecutions, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the dispatch-aware tool.",
            runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        var execution = Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status);
        Assert.Null(execution.StartedAtUtc);
    }

    [Fact]
    public async Task DispatchAwareTool_PreDispatchCallerCancellationRecoversAsDefinitiveFailure()
    {
        const string toolId = "dispatch_aware_cancellation";
        var provider = new ScriptedProvider((_, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new DispatchAwareToolSource(toolId, DispatchAwareToolBehavior.CancelBeforeDispatch);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ObservedToolExecutions, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var cancellation = new CancellationTokenSource();

        var run = runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Cancel while connecting.",
            runtime.CurrentWorkspaceId,
            [],
            cancellation.Token);
        await source.ExecutionEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await cancellation.CancelAsync();

        var checkpoint = await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(AgentRunStatus.Interrupted, checkpoint.Status);
        var execution = Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status);
        Assert.Null(execution.StartedAtUtc);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DispatchAwareTool_AmbiguousOutcomeAfterDispatchPersistsStartedTransition(
        bool loseWorkerTransport)
    {
        const string toolId = "dispatch_aware_timeout";
        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            2 => AssertAmbiguousToolResultAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new DispatchAwareToolSource(
            toolId,
            loseWorkerTransport
                ? DispatchAwareToolBehavior.TransportLostAfterDispatch
                : DispatchAwareToolBehavior.AmbiguousAfterDispatch);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ObservedToolExecutions, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the remote mutation.",
            runtime.CurrentWorkspaceId);

        Assert.True(checkpoint.Status == AgentRunStatus.Completed,
            $"{checkpoint.Status}: {checkpoint.Summary}\n{runtime.ExtensionCatalog.LastInvocationFailure}");
        var execution = Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        Assert.Equal(AgentToolExecutionStatus.Ambiguous, execution.Status);
        Assert.NotNull(execution.StartedAtUtc);
    }

    [Fact]
    public async Task DispatchAwareTool_UnexpectedPostDispatchFailureIsAmbiguous()
    {
        const string toolId = "dispatch_aware_unexpected_failure";
        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            2 => AssertAmbiguousToolResultAndComplete(request, toolId, "call-1"),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new DispatchAwareToolSource(
            toolId,
            DispatchAwareToolBehavior.UnexpectedFailureAfterDispatch);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ObservedToolExecutions, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the remote mutation.",
            runtime.CurrentWorkspaceId);

        Assert.True(checkpoint.Status == AgentRunStatus.Completed,
            $"{checkpoint.Status}: {checkpoint.Summary}\n{runtime.ExtensionCatalog.LastInvocationFailure}");
        var execution = Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        Assert.Equal(AgentToolExecutionStatus.Ambiguous, execution.Status);
        Assert.NotNull(execution.StartedAtUtc);
    }

    [Fact]
    public async Task DispatchAwareTool_ExplicitPostDispatchFailureRemainsDefinitive()
    {
        const string toolId = "dispatch_aware_definitive_failure";
        const string errorCode = "test-definitive-failure";
        var provider = new ScriptedProvider((request, requestIndex) => requestIndex switch
        {
            1 => ToolRequest("call-1", toolId, "{}"),
            2 => AssertErroredToolResultAndComplete(
                request,
                toolId,
                "call-1",
                errorCode,
                "The backend definitively rejected the request."),
            _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
        });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new DispatchAwareToolSource(
            toolId,
            DispatchAwareToolBehavior.DefinitiveFailureAfterDispatch);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ObservedToolExecutions, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the remote mutation.",
            runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        var execution = Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status);
        Assert.NotNull(execution.StartedAtUtc);
        Assert.Equal(errorCode, execution.OutcomeCode);
    }

    [Fact]
    public async Task OutsideScopeApproval_PropagatesToPreflightAndExecution()
    {
        const string toolId = "outside_approved_tool";
        var provider = new ScriptedProvider(
            (request, requestIndex) => requestIndex switch
            {
                1 => ToolRequest("call-1", toolId, "{}"),
                2 => AssertAndComplete(request, toolId, "call-1"),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new PreflightPermissionedToolSource(toolId, deferInPreflight: false);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.PermissionService.SetSessionUnrestrictedMode(sessionId, isEnabled: true);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the outside-scope tool.",
            runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(1, source.PreflightCount);
        Assert.Equal(1, source.ExecutionCount);
        Assert.True(source.PreflightAllowedOutsideConfiguredScope);
        Assert.True(source.ExecutionAllowedOutsideConfiguredScope);
        var execution = Assert.Single(runtime.Store.ListToolExecutions(sessionId));
        Assert.Equal(AgentToolExecutionStatus.Completed, execution.Status);
        Assert.NotNull(execution.StartedAtUtc);
    }

    [Fact]
    public async Task ConfiguredScopeApproval_PropagatesResourceReferencesToPreflightAndExecution()
    {
        const string toolId = "configured_scope_approved_tool";
        const string resourceReference = "configured-resource-lease";
        var provider = new ScriptedProvider(
            (request, requestIndex) => requestIndex switch
            {
                1 => ToolRequest("call-1", toolId, "{}"),
                2 => AssertAndComplete(request, toolId, "call-1"),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new PreflightPermissionedToolSource(
            toolId,
            deferInPreflight: false,
            boundaryId: AgentPermissionBoundaryIds.ConfiguredScope,
            resourceReferences: [resourceReference]);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.PermissionService.SetSessionUnrestrictedMode(sessionId, isEnabled: true);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the configured-scope tool.",
            runtime.CurrentWorkspaceId);

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal([resourceReference], source.PreflightApprovedResourceReferences);
        Assert.Equal([resourceReference], source.ExecutionApprovedResourceReferences);
        Assert.False(source.PreflightAllowedOutsideConfiguredScope);
        Assert.False(source.ExecutionAllowedOutsideConfiguredScope);
    }

    [Fact]
    public async Task ApprovedToolPreflight_DefersBeforePermissionExecutionStarts()
    {
        const string toolId = "approved_preflight_deferred_tool";
        const string errorCode = "test-approved-preflight-deferred";
        var provider = new ScriptedProvider(
            (request, requestIndex) => requestIndex switch
            {
                1 => ToolRequest("call-1", toolId, "{}"),
                2 => AssertErroredToolResultAndComplete(
                    request,
                    toolId,
                    "call-1",
                    errorCode,
                    "No mutation was dispatched."),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            });
        using var runtime = AgentTestRuntime.Create(provider);
        var source = new PreflightPermissionedToolSource(
            toolId,
            deferInPreflight: true,
            deferredErrorCode: errorCode);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, source);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, source);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var waiting = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Run the approved preflight tool.",
            runtime.CurrentWorkspaceId);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));
        var completed = await runtime.RunCoordinator.ApprovePendingPermissionAsync(sessionId, pending.RequestId);

        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.True(
            completed?.Status == AgentRunStatus.Completed,
            completed?.Summary ?? completed?.Status.ToString() ?? "No checkpoint returned.");
        Assert.Equal(1, source.PreflightCount);
        Assert.Equal(0, source.ExecutionCount);
        Assert.True(source.PreflightAllowedOutsideConfiguredScope);
        var persistedPermission = Assert.IsType<AgentPendingPermissionRequestRecord>(
            runtime.Store.GetPermissionRequest(sessionId, pending.RequestId));
        Assert.Equal(AgentPendingPermissionStatus.Failed, persistedPermission.Status);
        Assert.Null(persistedPermission.ExecutionStartedAtUtc);
        var execution = Assert.IsType<AgentToolExecutionRecord>(
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value));
        Assert.Equal(AgentToolExecutionStatus.Failed, execution.Status);
        Assert.Null(execution.StartedAtUtc);
        Assert.Equal(errorCode, execution.OutcomeCode);
    }

    [Fact]
    public async Task ApprovePendingPermissionAsync_CommitsChildSuspensionAndPermissionOutcomeTogether()
    {
        const string toolId = "approval_child_tool";
        var provider = new ScriptedProvider(
            (_, requestIndex) => requestIndex == 1
                ? ToolRequest("call-1", toolId, "{}")
                : throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."));
        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId, waitsForChild: true);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var waiting = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Start approved child work.",
            runtime.CurrentWorkspaceId);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));
        var suspended = await runtime.RunCoordinator.ApprovePendingPermissionAsync(
            sessionId,
            pending.RequestId);

        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.Equal(AgentRunStatus.WaitingForApproval, suspended?.Status);
        Assert.Equal(
            AgentPendingPermissionStatus.Executed,
            runtime.Store.GetPermissionRequest(sessionId, pending.RequestId)?.Status);
        var execution = Assert.IsType<AgentToolExecutionRecord>(
            runtime.Store.GetToolExecution(pending.ToolExecutionId!.Value));
        Assert.Equal(AgentToolExecutionStatus.Completed, execution.Status);
        Assert.Equal("child-suspension-durable", execution.OutcomeCode);
        Assert.IsType<AgentChildJoinRunSuspension>(runtime.Store.GetRun(pending.RunId)?.Suspension);
        Assert.Single(
            runtime.SessionService.ListTurns(sessionId).SelectMany(turn => turn.Items),
            item => item.ToolExecutionId == execution.ExecutionId
                    && item.Kind == AgentTurnItemKind.ToolResult);

        var recovered = new AgentLocalStore(new TestPackageContext(runtime.RootPath));
        Assert.Equal(
            AgentPendingPermissionStatus.Executed,
            recovered.GetPermissionRequest(sessionId, pending.RequestId)?.Status);
        Assert.Equal(AgentToolExecutionStatus.Completed, recovered.GetToolExecution(execution.ExecutionId)?.Status);
        Assert.IsType<AgentChildJoinRunSuspension>(recovered.GetRun(pending.RunId)?.Suspension);
    }

    [Fact]
    public async Task ApprovePendingPermissionAsync_ReusesDurableProviderBudget()
    {
        const string toolId = "approval_tool";
        var provider = new ScriptedProvider(
            (_, requestIndex) => requestIndex == 1
                ? ToolRequest("call-1", toolId, "{\"path\":\"~\"}")
                : throw new Xunit.Sdk.XunitException(
                    $"Provider request {requestIndex} must be rejected by the cumulative budget."));
        using var runtime = AgentTestRuntime.CreateWithBudget(
            provider,
            new AgentRunBudgetLimits(TimeSpan.FromMinutes(5), 1, 10));
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var waiting = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.",
            runtime.CurrentWorkspaceId);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));

        var completed = await runtime.RunCoordinator.ApprovePendingPermissionAsync(
            sessionId,
            pending.RequestId);

        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        Assert.Equal(AgentRunStatus.Failed, completed?.Status);
        Assert.Contains("1-cycle provider budget", completed?.Summary, StringComparison.Ordinal);
        Assert.Single(provider.Requests);
        Assert.Equal(1, toolSource.ExecutionCount);
        Assert.Equal(2, runtime.Store.GetRun(pending.RunId)?.ProviderCycleCount);
    }

    [Fact]
    public async Task ApprovePendingPermissionAsync_UsesCoreSessionProjection_WhenContinuingProvider()
    {
        const string toolId = "approval_tool";
        var provider = new ScriptedProvider(
            (request, requestIndex) =>
                requestIndex switch
                {
                    1 => ToolRequest("call-1", toolId, "{\"path\":\"~\"}"),
                    2 => AssertPermissionResumeContextAndComplete(request),
                    _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
                }
        );
        provider.UsageHandler = (_, requestIndex) => requestIndex == 1
            ? Usage(totalTokens: 200_000)
            : Usage(totalTokens: 1_000);

        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 40; index++)
        {
            var role = index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant;
            runtime.SessionService.AppendTextTurn(sessionId, role, $"approval-old-{index:000}");
        }

        var waitingCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool with old context.",
            runtime.CurrentWorkspaceId
        );

        Assert.Equal(AgentRunStatus.WaitingForApproval, waitingCheckpoint.Status);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));
        var completedCheckpoint = await runtime.RunCoordinator.ApprovePendingPermissionAsync(sessionId, pending.RequestId);

        Assert.NotNull(completedCheckpoint);
        Assert.Equal(AgentRunStatus.Completed, completedCheckpoint!.Status);
    }

    [Fact]
    public async Task WorkspaceExecutionContext_RejectsStalePathSnapshot()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")));
        await runtime.CreateSessionAsync("noop");
        var snapshot = Assert.IsType<AgentWorkspaceRecord>(
            runtime.WorkspaceService.GetWorkspace(runtime.CurrentWorkspaceId));
        Assert.True(runtime.ToolService.IsCurrentWorkspaceExecutionContext(
            snapshot,
            executionBinding: null));
        var workspacePath = Path.Combine(runtime.RootPath, "workspace");
        Directory.CreateDirectory(workspacePath);

        runtime.WorkspaceService.SaveWorkspacePaths(
            runtime.CurrentWorkspaceId,
            [
                new AgentWorkspacePathRecord(
                    Guid.NewGuid().ToString("N"),
                    runtime.CurrentWorkspaceId,
                    workspacePath,
                    IsDefault: true,
                    SortOrder: 0,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);

        Assert.False(runtime.ToolService.IsCurrentWorkspaceExecutionContext(
            snapshot,
            executionBinding: null));
    }

    [Fact]
    public async Task ToolExecution_RechecksWorkspaceAfterAwaitedReadiness()
    {
        const string toolId = "blocking_readiness";
        var tool = new BlockingReadinessTool(toolId);
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var workspace = Assert.IsType<AgentWorkspaceRecord>(
            runtime.WorkspaceService.GetWorkspace(runtime.CurrentWorkspaceId));

        var execution = runtime.ToolService.ExecuteAsync(
            toolId,
            "{}",
            sessionId,
            runtime.CurrentProfileId,
            workspace);
        await tool.ReadinessEntered.WaitAsync(TimeSpan.FromSeconds(10));
        var workspacePath = Path.Combine(runtime.RootPath, "changed-workspace");
        Directory.CreateDirectory(workspacePath);
        runtime.WorkspaceService.SaveWorkspacePaths(
            runtime.CurrentWorkspaceId,
            [
                new AgentWorkspacePathRecord(
                    Guid.NewGuid().ToString("N"),
                    runtime.CurrentWorkspaceId,
                    workspacePath,
                    IsDefault: true,
                    SortOrder: 0,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]);
        tool.ReleaseReadiness();

        var result = await execution;

        Assert.True(result.IsError);
        Assert.Equal(AgentToolSecurityErrorCodes.PermissionContextChanged, result.ErrorCode);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task ToolDispatch_StartsAtomicallyBeforeConcurrentWorkspaceMutation()
    {
        const string toolId = "workspace_dispatch_fence";
        var tool = new WorkspaceDispatchFenceTool(toolId);
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var workspace = Assert.IsType<AgentWorkspaceRecord>(
            runtime.WorkspaceService.GetWorkspace(runtime.CurrentWorkspaceId));
        var changedPath = Path.Combine(runtime.RootPath, "changed-workspace");
        Directory.CreateDirectory(changedPath);
        tool.ConfigureMutation(() => runtime.WorkspaceService.SaveWorkspacePaths(
            runtime.CurrentWorkspaceId,
            [
                new AgentWorkspacePathRecord(
                    Guid.NewGuid().ToString("N"),
                    runtime.CurrentWorkspaceId,
                    changedPath,
                    IsDefault: true,
                    SortOrder: 0,
                    DateTimeOffset.UtcNow,
                    DateTimeOffset.UtcNow),
            ]));

        var result = await runtime.ToolService.ExecuteAsync(
            toolId,
            "{}",
            sessionId,
            runtime.CurrentProfileId,
            workspace);
        await Assert.IsType<Task>(tool.MutationTask);

        Assert.False(result.IsError, result.Summary);
        Assert.Equal(1, tool.ExecutionCount);
        Assert.False(runtime.ToolService.IsCurrentWorkspaceExecutionContext(
            workspace,
            executionBinding: null));
    }

    [Fact]
    public async Task PendingPermissionRequestsForSessionTree_IncludesChildRequests()
    {
        const string toolId = "approval_tool";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );

        runtime.PermissionService.SavePendingRequest(
            new AgentPendingPermissionRequestRecord(
                "request-1",
                childSession.SessionId,
                Guid.NewGuid(),
                1,
                runtime.CurrentProfileId,
                Guid.NewGuid(),
                "parent task",
                "call-1",
                PermissionedToolSource.ActionIdForTests,
                PermissionedToolSource.BoundaryIdForTests,
                "Execute approval tool",
                toolId,
                "{}",
                null,
                null,
                runtime.CurrentWorkspaceId,
                null,
                null,
                null,
                true,
                DateTimeOffset.UtcNow,
                childSession.ParentSessionId,
                childSession.RootSessionId
            )
        );

        var pending = runtime.PermissionService.ListPendingRequestsForSessionTree(parentSessionId);

        var request = Assert.Single(pending);
        Assert.Equal(childSession.SessionId, request.SessionId);
    }

    [Fact]
    public async Task PermissionEvaluation_InheritsSessionApprovalFromParentSession()
    {
        const string toolId = "approval_tool";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, requestIndex) => requestIndex switch
            {
                1 => ToolRequest("approval-call", toolId, "{}"),
                2 => Complete("done"),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            })
        );
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.ToolSources,
            toolSource
        );
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.PermissionSurfaces,
            toolSource
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var waiting = await runtime.RunCoordinator.QueueUserMessageAsync(
            parentSessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.",
            runtime.CurrentWorkspaceId
        );
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(parentSessionId));
        var completed = await runtime.RunCoordinator.ApprovePendingPermissionAsync(
            parentSessionId,
            pending.RequestId,
            approveForSession: true,
            CancellationToken.None
        );
        Assert.Equal(AgentRunStatus.Completed, completed?.Status);
        Assert.Single(runtime.Store.ListSessionPermissionApprovals(parentSessionId));

        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );

        var evaluation = await runtime.PermissionService.EvaluateAsync(
            childSession.SessionId,
            new AgentPermissionRequest(
                PermissionedToolSource.ActionIdForTests,
                PermissionedToolSource.BoundaryIdForTests,
                "Execute approval tool"
            )
        );

        Assert.Equal(AgentPermissionDecision.Allow, evaluation.Decision);
        Assert.Equal(AgentPermissionDecision.Ask, evaluation.BaseDecision);
        Assert.Equal(AgentPermissionDecisionSource.SessionApproval, evaluation.Source);
        Assert.Equal(parentSessionId, evaluation.SourceSessionId);
        Assert.Contains("session-scoped approval", evaluation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StopAsync_ClearsPendingPermissionRequestsForStoppedSubsessions()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var parentSessionId = await runtime.CreateSessionAsync("approval_tool");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.SaveCheckpoint(
            parentSessionId,
            1,
            AgentRunStatus.Running,
            "Parent running."
        );
        runtime.SessionService.SaveCheckpoint(
            childSession.SessionId,
            1,
            AgentRunStatus.WaitingForApproval,
            "Child waiting."
        );
        runtime.PermissionService.SavePendingRequest(
            new AgentPendingPermissionRequestRecord(
                "request-1",
                childSession.SessionId,
                Guid.NewGuid(),
                1,
                runtime.CurrentProfileId,
                Guid.NewGuid(),
                "Use the approval tool.",
                "call-1",
                "approval_action",
                "approval_boundary",
                "Approve child tool use.",
                "approval_tool",
                "{}",
                null,
                null,
                runtime.CurrentWorkspaceId,
                null,
                null,
                null,
                true,
                DateTimeOffset.UtcNow,
                childSession.ParentSessionId,
                childSession.RootSessionId
            )
        );
        var observedPendingCounts = new List<int>();
        runtime.SessionService.SessionChanged += changedSessionId =>
        {
            if (changedSessionId == childSession.SessionId)
            {
                observedPendingCounts.Add(
                    runtime.PermissionService.ListPendingRequests(changedSessionId).Count);
            }
        };

        await runtime.RunCoordinator.StopAsync(parentSessionId);

        Assert.Empty(runtime.PermissionService.ListPendingRequests(childSession.SessionId));
        Assert.Contains(1, observedPendingCounts);
        Assert.Equal(0, observedPendingCounts[^1]);
        Assert.Equal(
            AgentRunStatus.Stopped,
            runtime.SessionService.GetLatestCheckpoint(childSession.SessionId)!.Status
        );
    }

    [Fact]
    public async Task DenyPendingPermission_AppendsErrorToolResultAndAllowsNextMessage()
    {
        const string toolId = "approval_tool";

        var provider = new ScriptedProvider(
            (request, requestIndex) =>
                requestIndex switch
                {
                    1 => ToolRequest("call-1", toolId, "{\"path\":\"~\"}"),
                    2 => AssertDeniedToolResultAndComplete(request, toolId, "call-1"),
                    _ => throw new Xunit.Sdk.XunitException(
                        $"Unexpected provider request {requestIndex}."
                    ),
                }
        );

        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.PermissionSurfaces,
            toolSource
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var waitingCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.",
            runtime.CurrentWorkspaceId
        );

        Assert.Equal(AgentRunStatus.WaitingForApproval, waitingCheckpoint.Status);
        var pending = Assert.Single(runtime.PermissionService.ListPendingRequests(sessionId));

        var deniedCheckpoint = await runtime.RunCoordinator.DenyPendingPermissionAsync(
            sessionId,
            pending.RequestId
        );

        Assert.NotNull(deniedCheckpoint);
        Assert.Equal(AgentRunStatus.Stopped, deniedCheckpoint!.Status);
        Assert.Empty(runtime.PermissionService.ListPendingRequests(sessionId));
        Assert.Equal(0, toolSource.ExecutionCount);
        Assert.Contains(
            runtime.SessionService.ListTurns(sessionId),
            turn =>
                turn.Kind == AgentTurnKind.ToolResult
                && turn.Items.Any(item =>
                    item.Kind == AgentTurnItemKind.ToolResult
                    && item.CallId == "call-1"
                    && item.ToolId == toolId
                    && item.IsError
                    && item.ErrorCode == "permission-denied"
                )
        );

        var completedCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Now write a calm poem.",
            runtime.CurrentWorkspaceId
        );

        Assert.Equal(AgentRunStatus.Completed, completedCheckpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(0, toolSource.ExecutionCount);
    }

    [Fact]
    public async Task DenyPendingPermission_PublishesCompletionBeforeToolResult()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")));
        var sessionId = await runtime.CreateSessionAsync("approval_tool");
        var run = runtime.Store.ReserveRun(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.");
        var lease = new AgentDurableRunLease(run);
        Assert.NotNull(runtime.SessionService.TryTransitionRun(
            lease,
            AgentRunStatus.Running,
            "Running."));
        var streamingTurn = runtime.SessionService.AppendTextTurn(
            lease,
            AgentMessageRole.Assistant,
            "partial preamble");
        var request = new AgentPendingPermissionRequestRecord(
            Guid.NewGuid().ToString("N"),
            sessionId,
            run.Key.RunId,
            run.Key.RunRevision,
            runtime.CurrentProfileId,
            Guid.NewGuid(),
            "Use the approval tool.",
            "call-1",
            "approval_action",
            "approval_boundary",
            "Approve tool use.",
            "approval_tool",
            "{}",
            null,
            null,
            runtime.CurrentWorkspaceId,
            null,
            null,
            null,
            true,
            DateTimeOffset.UtcNow,
            ExecutionFingerprint: new string('a', 64));
        Assert.NotNull(runtime.SessionService.SavePendingPermissionRequestAndSuspendRun(request, lease));
        var mutations = new List<AgentTurnMutation>();
        runtime.SessionService.TurnMutated += mutation =>
        {
            mutations.Add(mutation);
            if (mutation.Kind == AgentTurnMutationKind.Complete)
            {
                runtime.SessionService.AppendTextTurn(
                    sessionId,
                    AgentMessageRole.User,
                    "reentrant message");
            }
        };

        var checkpoint = await runtime.RunCoordinator.DenyPendingPermissionAsync(
            sessionId,
            request.RequestId);

        Assert.Equal(AgentRunStatus.Stopped, checkpoint?.Status);
        Assert.Equal(
            [
                AgentTurnMutationKind.Complete,
                AgentTurnMutationKind.Add,
                AgentTurnMutationKind.Add,
            ],
            mutations.Select(mutation => mutation.Kind));
        Assert.Equal(streamingTurn.TurnId, mutations[0].TurnId);
        Assert.False(Assert.IsType<AgentTurnRecord>(runtime.Store.GetTurn(streamingTurn.TurnId)).IsStreaming);
        Assert.Equal(AgentTurnKind.ToolResult, mutations[1].Turn?.Kind);
        Assert.Equal(AgentTurnKind.Message, mutations[2].Turn?.Kind);
    }

    [Fact]
    public async Task QueueUserMessageAsync_DropsHistoricalToolCallWithoutResult()
    {
        const string toolId = "fetch_page";
        const string orphanCallId = "orphan-call";

        var provider = new ScriptedProvider(
            (request, requestIndex) =>
            {
                Assert.Equal(1, requestIndex);
                Assert.DoesNotContain(
                    request.Turns,
                    turn =>
                        turn.Kind == AgentTurnKind.ToolCall
                        && turn.Items.Any(item =>
                            item.Kind == AgentTurnItemKind.ToolCall && item.CallId == orphanCallId
                        )
                );
                AssertNoUnpairedToolItems(request);
                return Complete("done");
            }
        );

        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "older request");
        runtime.SessionService.AppendToolCallTurn(
            sessionId,
            AgentMessageRole.Assistant,
            orphanCallId,
            toolId,
            "{\"url\":\"https://example.com\"}"
        );

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Continue without the old tool call.",
            runtime.CurrentWorkspaceId
        );

        Assert.True(
            checkpoint.Status == AgentRunStatus.Completed,
            checkpoint.Summary ?? checkpoint.Status.ToString());
    }

    [Theory]
    [InlineData(5000, 5000)]
    [InlineData(null, 15000)]
    public void ResolveEffectiveTimeoutMilliseconds_UsesConfiguredOrFiniteDefaultTimeout(
        int? serverTimeoutMilliseconds,
        int? expectedTimeoutMilliseconds
    )
    {
        var resolvedTimeout = McpTimeoutResolver.ResolveEffectiveTimeoutMilliseconds(
            serverTimeoutMilliseconds
        );

        Assert.Equal(expectedTimeoutMilliseconds, resolvedTimeout);
    }

    [Fact]
    public async Task AgentPermissionPanelState_DenyActionRefreshesRequestsAndRunState()
    {
        const string toolId = "approval_tool";
        var provider = new ScriptedProvider(
            (request, requestIndex) => requestIndex switch
            {
                1 => ToolRequest("permission-call", toolId, "{\"path\":\"~\"}"),
                _ => throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."),
            }
        );
        using var runtime = AgentTestRuntime.Create(provider);
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.ToolSources, toolSource);
        runtime.ExtensionCatalog.AddProvider(RegressionTestBindings.PermissionSurfaces, toolSource);
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var waiting = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the approval tool.",
            runtime.CurrentWorkspaceId
        );
        Assert.Equal(AgentRunStatus.WaitingForApproval, waiting.Status);

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        var request = Assert.Single(viewModel.PendingPermissionRequests);

        await viewModel.DenyPermissionCommand.ExecuteAsync(request);

        Assert.Empty(viewModel.PendingPermissionRequests);
        Assert.False(viewModel.HasPendingPermissionRequests);
        Assert.Equal(
            AgentRunStatus.Stopped,
            runtime.SessionService.GetLatestCheckpoint(sessionId)?.Status
        );
    }
}
