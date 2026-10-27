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
    public async Task ParentRunContinuation_UsesCoreSessionProjection_WhenChildCompletes()
    {
        const string toolId = "fetch_page";
        var provider = new ScriptedProvider(
            (request, requestIndex) =>
            {
                if (requestIndex == 1)
                {
                    return Complete("seeded parent context");
                }

                Assert.Equal(2, requestIndex);
                var systemInstructions = request.SystemInstructions ?? string.Empty;
                Assert.DoesNotContain("parent-old-000", systemInstructions, StringComparison.Ordinal);
                Assert.True(
                    request.Turns.Any(turn => turn.Role == AgentMessageRole.User
                                              && RenderTurnText(turn).Contains("Supplementary user-role context follows as JSON", StringComparison.Ordinal)
                                              && RenderTurnText(turn).Contains("parent-old-000", StringComparison.Ordinal)),
                    "The parent continuation request did not include projected supplementary context.");
                Assert.DoesNotContain(request.Turns, turn => RenderTurnText(turn) == "parent-old-000");
                Assert.True(
                    request.Turns.Any(turn => turn.Kind == AgentTurnKind.ToolResult
                                              && turn.Items.Any(item => item.CallId == "task-call")),
                    "The parent continuation request did not include the completed child tool result.");
                return Complete("parent resumed");
            }
        );
        provider.UsageHandler = (_, requestIndex) => requestIndex == 1
            ? Usage(totalTokens: 200_000)
            : Usage(totalTokens: 1_000);

        using var runtime = AgentTestRuntime.Create(provider, new TestTool(toolId));
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        runtime.SessionService.UpdateSession(parentSession with { ProfileId = runtime.CurrentProfileId });
        for (var index = 0; index < 40; index++)
        {
            var role = index % 2 == 0 ? AgentMessageRole.User : AgentMessageRole.Assistant;
            runtime.SessionService.AppendTextTurn(parentSessionId, role, $"parent-old-{index:000}");
        }

        var seedCheckpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            parentSessionId,
            runtime.CurrentProfileId,
            "Prepare parent context.",
            runtime.CurrentWorkspaceId);
        Assert.Equal(AgentRunStatus.Completed, seedCheckpoint.Status);

        var parentUserTurn = runtime.SessionService.AppendTextTurn(
            parentSessionId,
            AgentMessageRole.User,
            "Parent task that delegated work.");
        var parentRun = runtime.SessionService.ReserveRun(
            parentSessionId,
            runtime.CurrentProfileId,
            "Parent task that delegated work.");
        var parentRunId = parentRun.Key.RunId;
        var parentRunRevision = parentRun.Key.RunRevision;
        runtime.SessionService.SaveCheckpoint(
            parentSessionId,
            parentRunRevision,
            AgentRunStatus.Running,
            "Parent running.");
        Assert.NotNull(runtime.Store.TryAppendToolCallTurn(parentRun.Key, runtime.Store.GetRun(parentRun.Key.RunId)!.Epoch,
            AgentMessageRole.Assistant, "task-call", "task", "{}"));
        var childSession = runtime.SessionService.CreateSession(
            "Child task",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId,
            parentRunId: parentRunId,
            parentRunRevision: parentRunRevision,
            parentToolCallId: "task-call",
            profileId: runtime.CurrentProfileId);
        Assert.NotNull(runtime.SessionService.SuspendRun(
            parentRun.Key,
            new AgentChildJoinRunSuspension(
                parentUserTurn.TurnId,
                "task",
                "{}",
                [new AgentChildJoinTask(childSession.SessionId, "task-call", childSession.Title)],
                []),
            "Waiting for child."));
        runtime.SessionService.UpdateSession(
            runtime.SessionService.GetSession(parentSessionId)! with { ProfileId = "mutable-session-profile" });
        runtime.SessionService.AppendTextTurn(childSession.SessionId, AgentMessageRole.Assistant, "Child task result.");
        var childCheckpoint = runtime.SessionService.SaveCheckpoint(childSession.SessionId, 1, AgentRunStatus.Completed, "Child completed.");

        var resumedCheckpoint = await runtime.ParentRunContinuationService.TryResumeAfterChildCompletionAsync(
            childSession,
            childCheckpoint,
            runtime.CurrentWorkspaceId,
            CancellationToken.None);

        Assert.NotNull(resumedCheckpoint);
        Assert.True(
            resumedCheckpoint!.Status == AgentRunStatus.Completed,
            resumedCheckpoint.Summary);
        Assert.Contains(runtime.SessionService.ListTurns(parentSessionId), turn => turn.TurnId == parentUserTurn.TurnId);
    }

    [Fact]
    public async Task ParentRunContinuation_ConcurrentChildrenConsumeOneFinalDurableWorkItem()
    {
        var provider = new ScriptedProvider((_, requestIndex) =>
            requestIndex == 1
                ? Complete("parent resumed once")
                : throw new Xunit.Sdk.XunitException($"Unexpected provider request {requestIndex}."));
        using var runtime = AgentTestRuntime.Create(provider);
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        runtime.SessionService.UpdateSession(parentSession with { ProfileId = runtime.CurrentProfileId });
        var userTurn = runtime.SessionService.AppendTextTurn(
            parentSessionId,
            AgentMessageRole.User,
            "Delegate two tasks.");
        var run = runtime.SessionService.ReserveRun(
            parentSessionId,
            runtime.CurrentProfileId,
            "Delegate two tasks.");
        var running = Assert.IsType<AgentRunTransitionResult>(runtime.Store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Parent running."));
        runtime.SessionService.AppendToolCallTurn(
            parentSessionId,
            AgentMessageRole.Assistant,
            "task-call",
            "delegate_tasks",
            "{}");
        var firstChild = runtime.SessionService.CreateSession(
            "First child",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId,
            parentRunId: run.Key.RunId,
            parentRunRevision: run.Key.RunRevision,
            parentToolCallId: "task-call",
            profileId: runtime.CurrentProfileId);
        var secondChild = runtime.SessionService.CreateSession(
            "Second child",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId,
            parentRunId: run.Key.RunId,
            parentRunRevision: run.Key.RunRevision,
            parentToolCallId: "task-call",
            profileId: runtime.CurrentProfileId);
        Assert.NotNull(runtime.Store.SuspendRun(
            run.Key,
            running.Run.Epoch,
            new AgentChildJoinRunSuspension(
                userTurn.TurnId,
                "delegate_tasks",
                "{}",
                [
                    new AgentChildJoinTask(firstChild.SessionId, "task-call", firstChild.Title),
                    new AgentChildJoinTask(secondChild.SessionId, "task-call", secondChild.Title),
                ],
                []),
            "Waiting for children."));
        runtime.SessionService.AppendTextTurn(firstChild.SessionId, AgentMessageRole.Assistant, "First result.");
        runtime.SessionService.AppendTextTurn(secondChild.SessionId, AgentMessageRole.Assistant, "Second result.");
        var firstCheckpoint = runtime.SessionService.SaveCheckpoint(
            firstChild.SessionId,
            1,
            AgentRunStatus.Completed,
            "First completed.");
        var secondCheckpoint = runtime.SessionService.SaveCheckpoint(
            secondChild.SessionId,
            1,
            AgentRunStatus.Completed,
            "Second completed.");
        var unwindingHandle = new AgentActiveRunHandle(
            run.Key.RunId,
            run.Key.RunRevision,
            run.StartedAtUtc,
            run.ProfileId,
            run.UserMessage,
            new CancellationTokenSource());
        Assert.True(runtime.ActiveRunRegistry.Activate(parentSessionId, unwindingHandle).IsAccepted);

        await Task.WhenAll(
            runtime.ParentRunContinuationService.TryResumeAfterChildCompletionAsync(
                firstChild,
                firstCheckpoint,
                runtime.CurrentWorkspaceId,
                CancellationToken.None),
            runtime.ParentRunContinuationService.TryResumeAfterChildCompletionAsync(
                secondChild,
                secondCheckpoint,
                runtime.CurrentWorkspaceId,
                CancellationToken.None));

        Assert.Empty(provider.Requests);
        runtime.ActiveRunRegistry.CleanupCurrent(
            parentSessionId,
            run.Key.RunId,
            run.Key.RunRevision);
        unwindingHandle.CancellationTokenSource.Dispose();
        await WaitUntilAsync(() => provider.Requests.Count == 1
            && runtime.Store.GetRun(run.Key.RunId)?.Status == AgentDurableRunStatus.Completed);

        Assert.Single(provider.Requests);
        Assert.Equal(AgentDurableRunStatus.Completed, runtime.Store.GetRun(run.Key.RunId)?.Status);
        Assert.Single(
            runtime.SessionService.ListTurns(parentSessionId)
                .SelectMany(turn => turn.Items),
            item => item.Kind == AgentTurnItemKind.ToolResult && item.CallId == "task-call");
        Assert.Empty(runtime.Store.ListDispatchableParentContinuationWork());
    }

    [Fact]
    public async Task ParentRunContinuation_CancellationAfterExecutionStartsInterruptsDurableRun()
    {
        var executionStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            executionStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        }

        var provider = new ScriptedProvider(
            (_, _) => Complete("must not complete"),
            beforeExecutionHandler: WaitForCancellationAsync);
        using var runtime = AgentTestRuntime.Create(provider);
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        runtime.SessionService.UpdateSession(parentSession with { ProfileId = runtime.CurrentProfileId });
        var userTurn = runtime.SessionService.AppendTextTurn(
            parentSessionId,
            AgentMessageRole.User,
            "Delegate one task.");
        var run = runtime.SessionService.ReserveRun(
            parentSessionId,
            runtime.CurrentProfileId,
            "Delegate one task.");
        var running = Assert.IsType<AgentRunTransitionResult>(runtime.Store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Parent running."));
        runtime.SessionService.AppendToolCallTurn(
            parentSessionId,
            AgentMessageRole.Assistant,
            "task-call",
            "task",
            "{}");
        var child = runtime.SessionService.CreateSession(
            "Child",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId,
            parentRunId: run.Key.RunId,
            parentRunRevision: run.Key.RunRevision,
            parentToolCallId: "task-call",
            profileId: runtime.CurrentProfileId);
        Assert.NotNull(runtime.Store.SuspendRun(
            run.Key,
            running.Run.Epoch,
            new AgentChildJoinRunSuspension(
                userTurn.TurnId,
                "task",
                "{}",
                [new AgentChildJoinTask(child.SessionId, "task-call", child.Title)],
                []),
            "Waiting for child."));
        runtime.SessionService.AppendTextTurn(child.SessionId, AgentMessageRole.Assistant, "Child result.");
        var childCheckpoint = runtime.SessionService.SaveCheckpoint(
            child.SessionId,
            1,
            AgentRunStatus.Completed,
            "Child completed.");

        var continuation = runtime.ParentRunContinuationService.TryResumeAfterChildCompletionAsync(
            child,
            childCheckpoint,
            runtime.CurrentWorkspaceId,
            CancellationToken.None);
        await executionStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var active = runtime.ActiveRunRegistry.GetCurrent(
            parentSessionId,
            run.Key.RunId,
            run.Key.RunRevision);
        Assert.NotNull(active);
        Assert.Equal(run.StartedAtUtc, active!.StartedAtUtc);
        active.CancellationTokenSource.Cancel();

        var checkpoint = await continuation.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(AgentRunStatus.Interrupted, checkpoint?.Status);
        Assert.Equal(AgentDurableRunStatus.Interrupted, runtime.Store.GetRun(run.Key.RunId)?.Status);
        Assert.Empty(runtime.Store.ListDispatchableParentContinuationWork());
    }

    [Fact]
    public async Task ParentRunContinuation_StaleReadyWorkIsInterruptedWithoutTranscriptMutation()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("must not execute")));
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        runtime.SessionService.UpdateSession(parentSession with { ProfileId = runtime.CurrentProfileId });
        var userTurn = runtime.SessionService.AppendTextTurn(
            parentSessionId,
            AgentMessageRole.User,
            "Delegate stale task.");
        var run = runtime.SessionService.ReserveRun(
            parentSessionId,
            runtime.CurrentProfileId,
            "Delegate stale task.");
        var running = Assert.IsType<AgentRunTransitionResult>(runtime.Store.TryTransitionRun(
            run.Key,
            run.Epoch,
            AgentRunStatus.Running,
            "Parent running."));
        runtime.SessionService.AppendToolCallTurn(
            parentSessionId,
            AgentMessageRole.Assistant,
            "task-call",
            "task",
            "{}");
        var child = runtime.SessionService.CreateSession(
            "Child",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId,
            parentRunId: run.Key.RunId,
            parentRunRevision: run.Key.RunRevision,
            parentToolCallId: "task-call",
            profileId: runtime.CurrentProfileId);
        var suspended = Assert.IsType<AgentRunSuspensionResult>(runtime.Store.SuspendRun(
            run.Key,
            running.Run.Epoch,
            new AgentChildJoinRunSuspension(
                userTurn.TurnId,
                "task",
                "{}",
                [new AgentChildJoinTask(child.SessionId, "task-call", child.Title)],
                []),
            "Waiting for child."));
        var ready = runtime.Store.CompleteChildJoinTask(
            run.Key,
            suspended.ContinuationToken,
            new AgentChildJoinTaskResult(
                child.SessionId,
                "task-call",
                AgentRunStatus.Completed,
                "Child completed.",
                "Child result.",
                child.Title));
        Assert.True(ready.IsReady);
        _ = runtime.SessionService.ReserveRun(
            parentSessionId,
            runtime.CurrentProfileId,
            "Newer message.");

        await runtime.ParentRunContinuationService.ProcessPendingWorkAsync(CancellationToken.None);

        Assert.Equal(AgentDurableRunStatus.Interrupted, runtime.Store.GetRun(run.Key.RunId)?.Status);
        Assert.DoesNotContain(
            runtime.SessionService.ListTurns(parentSessionId).SelectMany(turn => turn.Items),
            item => item.Kind == AgentTurnItemKind.ToolResult && item.CallId == "task-call");
        Assert.Empty(runtime.Store.ListDispatchableParentContinuationWork());
    }

    [Fact]
    public async Task ChildRunResult_RejectsCheckpointFromDifferentSession()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("unused")));
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var child = runtime.SessionService.CreateSession(
            "Child",
            parentSessionId: parentSessionId,
            rootSessionId: parentSessionId,
            profileId: runtime.CurrentProfileId);
        var foreignCheckpoint = new AgentRunCheckpointRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            1,
            AgentRunStatus.Completed,
            "Foreign checkpoint.",
            DateTimeOffset.UtcNow);
        var childSessions = new AgentChildRunSessionService(
            runtime.SessionService,
            runtime.ProfileService);

        Assert.Throws<InvalidOperationException>(() =>
            childSessions.BuildResult(child, foreignCheckpoint));
        Assert.Null(await runtime.ParentRunContinuationService.TryResumeAfterChildCompletionAsync(
            child,
            foreignCheckpoint,
            runtime.CurrentWorkspaceId,
            CancellationToken.None));
    }

    [Fact]
    public async Task PermissionEvaluation_InheritsUnrestrictedModeFromParentSession()
    {
        const string toolId = "approval_tool";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var toolSource = new PermissionedToolSource(toolId);
        runtime.ExtensionCatalog.AddProvider(
            RegressionTestBindings.PermissionSurfaces,
            toolSource
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
        runtime.PermissionService.SetSessionUnrestrictedMode(parentSessionId, true);

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
        Assert.Equal(AgentPermissionDecisionSource.UnrestrictedMode, evaluation.Source);
        Assert.Equal(parentSessionId, evaluation.SourceSessionId);
        Assert.Contains("inherited Unrestricted Mode", evaluation.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void SubagentService_SaveSubagent_RequiresDescription()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-description-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");

            Assert.False(subagent.HasRequiredDescription);
            var ex = Assert.Throws<SubagentOwnerOperationException>(
                () =>
                    SaveSubagent(service,
                        subagent.SubagentId,
                        subagent.DisplayName,
                        " ",
                        subagent.Instructions,
                        null,
                        null,
                        []
                    )
            );
            Assert.Contains(
                "description is required",
                ex.Message,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public void SubagentService_SaveSubagent_ClearsModelSettingsWhenProviderInherits()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-model-inherit-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");

            var saved = SaveSubagent(service,
                subagent.SubagentId,
                subagent.DisplayName,
                "Investigates delegated research tasks.",
                subagent.Instructions,
                null,
                "stale-model",
                [],
                "{\"reasoningVariantId\":\"high\"}"
            );

            Assert.Null(saved.ChatProviderId);
            Assert.Null(saved.ChatModelId);
            Assert.Null(saved.ChatModelSettingsJson);
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task SubagentFeature_ListCapabilitiesAsync_MarksIncompleteSubagentsUnavailable()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-capability-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var incomplete = service.CreateSubagent("Incomplete");
            var usable = service.CreateSubagent("Researcher");
            SaveSubagent(service,
                usable.SubagentId,
                usable.DisplayName,
                "Investigates delegated research tasks.",
                usable.Instructions,
                null,
                null,
                []
            );
            using var extensionCatalog = new TestExtensionCatalog();
            AddSubagentBehaviorLoop(extensionCatalog);
            var feature = extensionCatalog.CreateSubagentFeature(service);
            using var featureProbe = new GeneratedToolSourceProbe(
                extensionCatalog, feature, SubagentConstants.PackageId, withSelectableCapabilities: true);

            var capabilities = await featureProbe.ListCapabilitiesAsync(
                new AgentProfileSelectableCapabilityRequest(
                    CreateBehaviorLoopProfile(SubagentConstants.OrchestratedBehaviorLoopId)
                )
            );

            Assert.Contains(
                capabilities,
                capability =>
                    capability.CapabilityId == usable.SubagentId && capability.IsSelectable
            );
            var incompleteCapability = Assert.Single(
                capabilities,
                capability => capability.CapabilityId == incomplete.SubagentId
            );
            Assert.False(incompleteCapability.IsSelectable);
            Assert.Contains(
                "Description is required",
                incompleteCapability.StatusText,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task SubagentFeature_ListCapabilitiesAsync_HidesSubagents_WhenBehaviorLoopDoesNotSupportSubagents()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-capability-gating-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");
            SaveSubagent(service,
                subagent.SubagentId,
                subagent.DisplayName,
                "Investigates delegated research tasks.",
                subagent.Instructions,
                null,
                null,
                []
            );
            var extensionCatalog = new TestExtensionCatalog();
            extensionCatalog.AddBehaviorLoop(new TestBehaviorLoop(AgentBehaviorLoopIds.Default, "Default")
            );
            extensionCatalog.AddBehaviorLoop(new TestBehaviorLoop("feature-loop", "Feature Loop", [SubagentConstants.FeatureKind])
            );
            var feature = extensionCatalog.CreateSubagentFeature(service);
            using var featureProbe = new GeneratedToolSourceProbe(
                extensionCatalog, feature, SubagentConstants.PackageId, withSelectableCapabilities: true);

            var defaultCapabilities = await featureProbe.ListCapabilitiesAsync(
                new AgentProfileSelectableCapabilityRequest(
                    CreateBehaviorLoopProfile(AgentBehaviorLoopIds.Default)
                )
            );
            var featureCapabilities = await featureProbe.ListCapabilitiesAsync(
                new AgentProfileSelectableCapabilityRequest(CreateBehaviorLoopProfile("feature-loop"))
            );

            Assert.Empty(defaultCapabilities);
            Assert.Contains(featureCapabilities, capability => capability.CapabilityId == subagent.SubagentId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task SubagentFeature_ListToolsAsync_RendersDescriptionDrivenGuidance()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-tool-description-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");
            SaveSubagent(service,
                subagent.SubagentId,
                subagent.DisplayName,
                "Investigates delegated research tasks and reports concise findings.",
                subagent.Instructions,
                null,
                null,
                []
            );
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [
                    new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Subagent,
                        subagent.SubagentId,
                        SubagentConstants.PackageId
                    ),
                ],
                SubagentConstants.OrchestratedBehaviorLoopId
            );
            using var extensionCatalog = new TestExtensionCatalog();
            AddSubagentBehaviorLoop(extensionCatalog);
            var feature = extensionCatalog.CreateSubagentFeature(service);
            using var featureProbe = new GeneratedToolSourceProbe(
                extensionCatalog, feature, SubagentConstants.PackageId);

            var tools = await featureProbe.ListToolsAsync(
                new AgentToolSourceContext(
                    SessionId: null,
                    profile,
                    Workspace: null,
                    ExecutionBinding: null
                )
            );

            var taskTool = Assert.Single(
                tools,
                tool => tool.ToolId == SubagentConstants.TaskToolId
            );
            Assert.Contains(
                "matches one or more enabled subagent descriptions",
                taskTool.Description,
                StringComparison.Ordinal
            );
            Assert.Contains(
                "Investigates delegated research tasks",
                taskTool.Description,
                StringComparison.Ordinal
            );
            Assert.Contains(
                "Do not invent subagent purposes",
                taskTool.RuntimeInstructions,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain(
                "codebase",
                taskTool.Description,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task SubagentTaskTool_ReturnsConfigurationError_WhenSelectedSubagentIsIncomplete()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-incomplete-task-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Researcher");
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [
                    new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Subagent,
                        subagent.SubagentId,
                        SubagentConstants.PackageId
                    ),
                ],
                SubagentConstants.OrchestratedBehaviorLoopId
            );
            var session = new AgentSessionRecord(
                Guid.NewGuid(),
                "Parent Session",
                AgentSessionState.Active,
                now,
                now,
                ProfileId: profile.ProfileId,
                BehaviorLoopId: profile.BehaviorLoopId
            );
            var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
            using var extensionCatalog = new TestExtensionCatalog();
            AddSubagentBehaviorLoop(extensionCatalog);
            extensionCatalog.AddRuntimeCatalog(
                new TestRuntimeCatalog([profile], [session], [workspace])
            );
            extensionCatalog.AddChildRunExecutor(
                new CapturingChildRunExecutor()
            );
            var feature = extensionCatalog.CreateSubagentFeature(service);
            using var featureProbe = new GeneratedToolSourceProbe(
                extensionCatalog, feature, SubagentConstants.PackageId);

            var result = await featureProbe.ExecuteAsync(
                new AgentToolExecutionContext(
                    session.SessionId,
                    profile.ProfileId,
                    workspace,
                    RunId: Guid.NewGuid(),
                    RunRevision: 1,
                    UserTurnId: Guid.NewGuid(),
                    ToolCallId: "task-call"
                ),
                new AgentToolRequest(
                    SubagentConstants.TaskToolId,
                    $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"
                )
            );

            Assert.True(result.IsError);
            Assert.Equal("subagent-description-required", result.ErrorCode);
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task QueueUserMessageAsync_ContinuesProviderAfterFailedSubagentResult()
    {
        const string toolId = "failing_subagent";
        var provider = new ScriptedProvider(
            (request, requestIndex) =>
                requestIndex switch
                {
                    1 => ToolRequest("call-1", toolId, "{}"),
                    2 => AssertErroredToolResultAndComplete(
                        request,
                        toolId,
                        "call-1",
                        AgentToolResultErrorCodes.SubagentRunFailed,
                        "subagent failed"
                    ),
                    _ => throw new Xunit.Sdk.XunitException(
                        $"Unexpected provider request {requestIndex}."
                    ),
                }
        );
        var tool = new GenericErrorResultTool(
            toolId,
            AgentToolResultErrorCodes.SubagentRunFailed,
            "subagent failed"
        );
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync(toolId);

        var checkpoint = await runtime.RunCoordinator.QueueUserMessageAsync(
            sessionId,
            runtime.CurrentProfileId,
            "Use the failing subagent tool.",
            runtime.CurrentWorkspaceId
        );

        Assert.Equal(AgentRunStatus.Completed, checkpoint.Status);
        Assert.Equal(2, provider.Requests.Count);
        Assert.Equal(1, tool.ExecutionCount);
    }

    [Fact]
    public async Task SubagentTaskTool_UsesSubagentCapabilitySelection()
    {
        const string toolId = "fetch_page";
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-tests",
            Guid.NewGuid().ToString("N")
        );
        var store = new SubagentStore(new TestPackageContext(rootPath));
        var service = new SubagentService(store);
        var subagent = service.CreateSubagent("Researcher");
        SaveSubagent(service,
            subagent.SubagentId,
            subagent.DisplayName,
            "Research specialist",
            "Research things.",
            "override-provider",
            "override-model",
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Tool,
                    toolId
                ),
            ]
        );
        var profile = new AgentProfileRecord(
            "profile-1",
            "Parent",
            null,
            null,
            "provider",
            "model",
            null,
            null,
            now,
            now,
            [
                new AgentProfileModelBindingRecord(
                    "profile-1",
                    AgentModelCapabilityKinds.Chat,
                    "provider",
                    "model",
                    null,
                    now
                ),
            ],
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Subagent,
                    subagent.SubagentId,
                    SubagentConstants.PackageId
                ),
            ],
            SubagentConstants.OrchestratedBehaviorLoopId
        );
        var session = new AgentSessionRecord(
            Guid.NewGuid(),
            "Parent Session",
            AgentSessionState.Active,
            now,
            now,
            ProfileId: profile.ProfileId,
            BehaviorLoopId: profile.BehaviorLoopId
        );
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var childExecutor = new CapturingChildRunExecutor();
        var runtimeCatalog = new TestRuntimeCatalog([profile], [session], [workspace]);
        using var extensionCatalog = new TestExtensionCatalog();
        AddSubagentBehaviorLoop(extensionCatalog);
        extensionCatalog.AddRuntimeCatalog(runtimeCatalog);
        extensionCatalog.AddChildRunExecutor(childExecutor);
        var feature = extensionCatalog.CreateSubagentFeature(service);
        using var featureProbe = new GeneratedToolSourceProbe(
            extensionCatalog, feature, SubagentConstants.PackageId);

        var result = await featureProbe.ExecuteAsync(
            new AgentToolExecutionContext(
                session.SessionId,
                profile.ProfileId,
                workspace,
                RunId: Guid.NewGuid(),
                RunRevision: 1,
                UserTurnId: Guid.NewGuid(),
                ToolCallId: "task-call"
            ),
            new AgentToolRequest(
                SubagentConstants.TaskToolId,
                $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"
            )
        );

        Assert.False(result.IsError);
        Assert.NotNull(childExecutor.Request);
        Assert.Equal("override-provider", childExecutor.Request!.ChildProfile.ChatProviderId);
        Assert.Equal("override-model", childExecutor.Request.ChildProfile.ChatModelId);
        Assert.Contains(
            childExecutor.Request.ChildProfile.ModelBindings ?? [],
            binding =>
                binding.CapabilityKind == AgentModelCapabilityKinds.Chat
                && binding.ProviderId == "override-provider"
                && binding.ModelId == "override-model"
        );
        Assert.Contains(
            childExecutor.Request!.ChildProfile.SelectableCapabilityAssignments ?? [],
            assignment =>
                assignment.Kind == AgentProfileSelectableCapabilityKinds.Tool
                && assignment.CapabilityId == toolId
        );
        Assert.False(string.IsNullOrWhiteSpace(result.StructuredPayloadJson));
        using var payloadDocument = JsonDocument.Parse(result.StructuredPayloadJson!);
        Assert.Equal(
            "Researcher",
            payloadDocument.RootElement.GetProperty("subagentName").GetString()
        );
        Assert.Equal(
            "Research",
            payloadDocument.RootElement.GetProperty("childSessionTitle").GetString()
        );
        Assert.Equal(
            result.BackendId,
            Guid.Parse(payloadDocument.RootElement.GetProperty("childSessionId").GetString()!)
                .ToString("N")
        );
    }

    [Fact]
    public async Task SubagentTaskTool_InheritsParentChatModelSettings_WhenNoOverrideIsConfigured()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-inherit-settings-tests",
            Guid.NewGuid().ToString("N")
        );
        var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
        var subagent = service.CreateSubagent("Researcher");
        SaveSubagent(service,
            subagent.SubagentId,
            subagent.DisplayName,
            "Research specialist",
            "Research things.",
            null,
            null,
            []
        );
        const string parentSettingsJson = "{\"reasoningVariantId\":\"high\"}";
        var profile = new AgentProfileRecord(
            "profile-1",
            "Parent",
            null,
            null,
            "provider",
            "model",
            null,
            null,
            now,
            now,
            [
                new AgentProfileModelBindingRecord(
                    "profile-1",
                    AgentModelCapabilityKinds.Chat,
                    "provider",
                    "model",
                    parentSettingsJson,
                    now
                ),
            ],
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Subagent,
                    subagent.SubagentId,
                    SubagentConstants.PackageId
                ),
            ],
            SubagentConstants.OrchestratedBehaviorLoopId
        );
        var session = new AgentSessionRecord(
            Guid.NewGuid(),
            "Parent Session",
            AgentSessionState.Active,
            now,
            now,
            ProfileId: profile.ProfileId,
            BehaviorLoopId: profile.BehaviorLoopId
        );
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var childExecutor = new CapturingChildRunExecutor();
        using var extensionCatalog = new TestExtensionCatalog();
        AddSubagentBehaviorLoop(extensionCatalog);
        extensionCatalog.AddRuntimeCatalog(
            new TestRuntimeCatalog([profile], [session], [workspace])
        );
        extensionCatalog.AddChildRunExecutor(childExecutor);
        var feature = extensionCatalog.CreateSubagentFeature(service);
        using var featureProbe = new GeneratedToolSourceProbe(
            extensionCatalog, feature, SubagentConstants.PackageId);

        var result = await featureProbe.ExecuteAsync(
            new AgentToolExecutionContext(
                session.SessionId,
                profile.ProfileId,
                workspace,
                RunId: Guid.NewGuid(),
                RunRevision: 1,
                UserTurnId: Guid.NewGuid(),
                ToolCallId: "task-call"
            ),
            new AgentToolRequest(
                SubagentConstants.TaskToolId,
                $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"
            )
        );

        Assert.False(result.IsError);
        var chatBinding = Assert.Single(
            childExecutor.Request!.ChildProfile.ModelBindings!,
            binding => binding.CapabilityKind == AgentModelCapabilityKinds.Chat
        );
        Assert.Equal(parentSettingsJson, chatBinding.SettingsJson);
    }

    [Fact]
    public async Task SubagentTaskTool_UsesOverrideChatModelSettings_WhenOverrideIsConfigured()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-override-settings-tests",
            Guid.NewGuid().ToString("N")
        );
        var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
        var subagent = service.CreateSubagent("Researcher");
        const string overrideSettingsJson = "{\"reasoningVariantId\":\"low\"}";
        SaveSubagent(service,
            subagent.SubagentId,
            subagent.DisplayName,
            "Research specialist",
            "Research things.",
            "override-provider",
            "override-model",
            [],
            overrideSettingsJson
        );
        var profile = new AgentProfileRecord(
            "profile-1",
            "Parent",
            null,
            null,
            "provider",
            "model",
            null,
            null,
            now,
            now,
            [
                new AgentProfileModelBindingRecord(
                    "profile-1",
                    AgentModelCapabilityKinds.Chat,
                    "provider",
                    "model",
                    "{\"reasoningVariantId\":\"high\"}",
                    now
                ),
            ],
            [
                new AgentProfileSelectableCapabilityAssignmentRecord(
                    AgentProfileSelectableCapabilityKinds.Subagent,
                    subagent.SubagentId,
                    SubagentConstants.PackageId
                ),
            ],
            SubagentConstants.OrchestratedBehaviorLoopId
        );
        var session = new AgentSessionRecord(
            Guid.NewGuid(),
            "Parent Session",
            AgentSessionState.Active,
            now,
            now,
            ProfileId: profile.ProfileId,
            BehaviorLoopId: profile.BehaviorLoopId
        );
        var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
        var childExecutor = new CapturingChildRunExecutor();
        using var extensionCatalog = new TestExtensionCatalog();
        AddSubagentBehaviorLoop(extensionCatalog);
        extensionCatalog.AddRuntimeCatalog(
            new TestRuntimeCatalog([profile], [session], [workspace])
        );
        extensionCatalog.AddChildRunExecutor(childExecutor);
        var feature = extensionCatalog.CreateSubagentFeature(service);
        using var featureProbe = new GeneratedToolSourceProbe(
            extensionCatalog, feature, SubagentConstants.PackageId);

        var result = await featureProbe.ExecuteAsync(
            new AgentToolExecutionContext(
                session.SessionId,
                profile.ProfileId,
                workspace,
                RunId: Guid.NewGuid(),
                RunRevision: 1,
                UserTurnId: Guid.NewGuid(),
                ToolCallId: "task-call"
            ),
            new AgentToolRequest(
                SubagentConstants.TaskToolId,
                $"{{\"description\":\"Research\",\"prompt\":\"Do research\",\"subagent_type\":\"{subagent.SubagentId}\"}}"
            )
        );

        Assert.False(result.IsError);
        var chatBinding = Assert.Single(
            childExecutor.Request!.ChildProfile.ModelBindings!,
            binding => binding.CapabilityKind == AgentModelCapabilityKinds.Chat
        );
        Assert.Equal("override-provider", chatBinding.ProviderId);
        Assert.Equal("override-model", chatBinding.ModelId);
        Assert.Equal(overrideSettingsJson, chatBinding.SettingsJson);
    }

    [Fact]
    public async Task SubagentDelegateTasksTool_RunsReadOnlySubagentsConcurrently()
    {
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-batch-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var first = service.CreateSubagent("Researcher");
            SaveSubagent(service,
                first.SubagentId,
                first.DisplayName,
                "Investigates delegated research tasks.",
                first.Instructions,
                null,
                null,
                []
            );
            var second = service.CreateSubagent("Reviewer");
            SaveSubagent(service,
                second.SubagentId,
                second.DisplayName,
                "Reviews delegated findings for risks.",
                second.Instructions,
                null,
                null,
                []
            );
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [
                    new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Subagent,
                        first.SubagentId,
                        SubagentConstants.PackageId
                    ),
                    new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Subagent,
                        second.SubagentId,
                        SubagentConstants.PackageId
                    ),
                ],
                SubagentConstants.OrchestratedBehaviorLoopId
            );
            var session = new AgentSessionRecord(
                Guid.NewGuid(),
                "Parent Session",
                AgentSessionState.Active,
                now,
                now,
                ProfileId: profile.ProfileId,
                BehaviorLoopId: profile.BehaviorLoopId
            );
            var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
            var childExecutor = new CapturingChildRunExecutor();
            using var extensionCatalog = new TestExtensionCatalog();
            AddSubagentBehaviorLoop(extensionCatalog);
            extensionCatalog.AddRuntimeCatalog(
                new TestRuntimeCatalog([profile], [session], [workspace])
            );
            extensionCatalog.AddChildRunExecutor(childExecutor);
            var feature = extensionCatalog.CreateSubagentFeature(service);
            using var featureProbe = new GeneratedToolSourceProbe(
                extensionCatalog, feature, SubagentConstants.PackageId);
            var argumentsJson = JsonSerializer.Serialize(
                new
                {
                    tasks = new[]
                    {
                        new
                        {
                            description = "Research",
                            prompt = "Research the target.",
                            subagent_type = first.SubagentId,
                        },
                        new
                        {
                            description = "Review",
                            prompt = "Review the findings.",
                            subagent_type = second.SubagentId,
                        },
                    },
                }
            );

            var result = await featureProbe.ExecuteAsync(
                new AgentToolExecutionContext(
                    session.SessionId,
                    profile.ProfileId,
                    workspace,
                    RunId: Guid.NewGuid(),
                    RunRevision: 1,
                    UserTurnId: Guid.NewGuid(),
                    ToolCallId: "batch-call"
                ),
                new AgentToolRequest(SubagentConstants.DelegateTasksToolId, argumentsJson)
            );

            Assert.False(result.IsError);
            Assert.Equal(2, childExecutor.Requests.Count);
            Assert.Contains("<task_result", result.Content, StringComparison.Ordinal);
            Assert.Contains("Researcher", result.Content, StringComparison.Ordinal);
            Assert.Contains("Reviewer", result.Content, StringComparison.Ordinal);
            using var payloadDocument = JsonDocument.Parse(result.StructuredPayloadJson!);
            Assert.Equal(2, payloadDocument.RootElement.GetProperty("tasks").GetArrayLength());
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task SubagentDelegateTasksTool_RejectsMutatingSubagents()
    {
        const string toolId = "write_file";
        var now = DateTimeOffset.UtcNow;
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-batch-mutation-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var service = new SubagentService(new SubagentStore(new TestPackageContext(rootPath)));
            var subagent = service.CreateSubagent("Builder");
            SaveSubagent(service,
                subagent.SubagentId,
                subagent.DisplayName,
                "Implements delegated changes.",
                subagent.Instructions,
                null,
                null,
                [
                    new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Tool,
                        toolId
                    ),
                ]
            );
            var profile = new AgentProfileRecord(
                "profile-1",
                "Parent",
                null,
                null,
                "provider",
                "model",
                null,
                null,
                now,
                now,
                [],
                [
                    new AgentProfileSelectableCapabilityAssignmentRecord(
                        AgentProfileSelectableCapabilityKinds.Subagent,
                        subagent.SubagentId,
                        SubagentConstants.PackageId
                    ),
                ],
                SubagentConstants.OrchestratedBehaviorLoopId
            );
            var session = new AgentSessionRecord(
                Guid.NewGuid(),
                "Parent Session",
                AgentSessionState.Active,
                now,
                now,
                ProfileId: profile.ProfileId,
                BehaviorLoopId: profile.BehaviorLoopId
            );
            var workspace = new AgentWorkspaceRecord("workspace", "Workspace", null, now, now);
            var childExecutor = new CapturingChildRunExecutor();
            using var extensionCatalog = new TestExtensionCatalog();
            AddSubagentBehaviorLoop(extensionCatalog);
            extensionCatalog.AddRuntimeCatalog(
                new TestRuntimeCatalog([profile], [session], [workspace])
            );
            extensionCatalog.AddChildRunExecutor(childExecutor);
            extensionCatalog.AddTool(new TestMutableTool(toolId)
            );
            var feature = extensionCatalog.CreateSubagentFeature(service);
            using var featureProbe = new GeneratedToolSourceProbe(
                extensionCatalog, feature, SubagentConstants.PackageId);
            var argumentsJson = JsonSerializer.Serialize(
                new
                {
                    tasks = new[]
                    {
                        new
                        {
                            description = "Build",
                            prompt = "Implement the change.",
                            subagent_type = subagent.SubagentId,
                        },
                    },
                }
            );

            var result = await featureProbe.ExecuteAsync(
                new AgentToolExecutionContext(
                    session.SessionId,
                    profile.ProfileId,
                    workspace,
                    RunId: Guid.NewGuid(),
                    RunRevision: 1,
                    UserTurnId: Guid.NewGuid(),
                    ToolCallId: "batch-call"
                ),
                new AgentToolRequest(SubagentConstants.DelegateTasksToolId, argumentsJson)
            );

            Assert.True(result.IsError);
            Assert.Equal("subagent-batch-not-read-only", result.ErrorCode);
            Assert.Empty(childExecutor.Requests);
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task SubagentTaskPresentation_ResolvesSubagentIdToDisplayName()
    {
        var rootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-subagent-presentation-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var store = new SubagentStore(new TestPackageContext(rootPath));
            var service = new SubagentService(store);
            var subagent = service.CreateSubagent("Researcher");
            using var extensions = new TestExtensionCatalog();
            var feature = extensions.CreateSubagentFeature(service);
            using var featureProbe = new GeneratedToolSourceProbe(
                extensions, feature, SubagentConstants.PackageId);
            var argumentsJson = JsonSerializer.Serialize(
                new
                {
                    description = "Research",
                    prompt = "Do research",
                    subagent_type = subagent.SubagentId,
                }
            );

            var presentation = await featureProbe.PresentationAsync(
                new AgentToolPresentationRequest(
                    SubagentConstants.TaskToolId,
                    argumentsJson,
                    ResultSummary: null,
                    TextContent: null,
                    StructuredPayloadJson: null,
                    SourcesJson: null,
                    IsError: false,
                    ErrorCode: null,
                    BackendId: null
                )
            );

            Assert.NotNull(presentation);
            Assert.Equal("Researcher subagent · Research", presentation!.HeaderText);
            Assert.Contains(
                "Subagent: Researcher subagent",
                presentation.DetailMarkdown,
                StringComparison.Ordinal
            );
            Assert.DoesNotContain(
                subagent.SubagentId,
                presentation.HeaderText,
                StringComparison.OrdinalIgnoreCase
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(rootPath))
                {
                    Directory.Delete(rootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public async Task RunChildAsync_ReusesChildSession_WhenTaskIdMatches()
    {
        var provider = new ScriptedProvider((_, requestIndex) => Complete($"done-{requestIndex}"));
        using var runtime = AgentTestRuntime.Create(provider);
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childProfile = runtime.CurrentProfile with
        {
            ProfileId = "child-profile",
            DisplayName = "Child Agent",
            ModelBindings =
            [
                new AgentProfileModelBindingRecord(
                    "child-profile",
                    AgentModelCapabilityKinds.Chat,
                    "test-provider",
                    "test-model",
                    null,
                    DateTimeOffset.UtcNow
                ),
            ],
            SelectableCapabilityAssignments = [],
            IsInternal = true,
        };

        var first = await runtime.RunCoordinator.RunChildAsync(
            new AgentChildRunRequest(
                parentSessionId,
                Guid.NewGuid(),
                1,
                "task-call-1",
                runtime.CurrentWorkspaceId,
                "task-123",
                childProfile,
                "First task prompt.",
                "Task 123"
            )
        );
        var second = await runtime.RunCoordinator.RunChildAsync(
            new AgentChildRunRequest(
                parentSessionId,
                Guid.NewGuid(),
                2,
                "task-call-2",
                runtime.CurrentWorkspaceId,
                "task-123",
                childProfile,
                "Second task prompt.",
                "Task 123"
            )
        );

        Assert.Equal(AgentRunStatus.Completed, first.Status);
        Assert.Equal(AgentRunStatus.Completed, second.Status);
        Assert.Equal(first.SessionId, second.SessionId);
        var childSession = Assert.Single(
            runtime.SessionService.ListSessions(),
            session =>
                session.ParentSessionId == parentSessionId
                && string.Equals(session.TaskId, "task-123", StringComparison.OrdinalIgnoreCase)
        );
        Assert.Equal(first.SessionId, childSession.SessionId);
        Assert.Equal(runtime.CurrentWorkspaceId, childSession.WorkspaceId);
        Assert.Equal(2, provider.Requests.Count);
    }

    [Fact]
    public async Task StopAsync_StopsParentSessionTree()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var parentSessionId = await runtime.CreateSessionAsync("noop");
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Child Session",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        var grandchildSession = runtime.SessionService.CreateSession(
            "Grandchild Session",
            parentSessionId: childSession.SessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        var completedChildSession = runtime.SessionService.CreateSession(
            "Completed Child Session",
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
            AgentRunStatus.Running,
            "Child running."
        );
        runtime.SessionService.SaveCheckpoint(
            grandchildSession.SessionId,
            1,
            AgentRunStatus.WaitingForApproval,
            "Grandchild waiting."
        );
        runtime.SessionService.SaveCheckpoint(
            completedChildSession.SessionId,
            1,
            AgentRunStatus.Completed,
            "Completed child."
        );

        var checkpoint = await runtime.RunCoordinator.StopAsync(parentSessionId);

        Assert.NotNull(checkpoint);
        Assert.Equal(AgentRunStatus.Stopped, checkpoint!.Status);
        Assert.Equal(
            AgentRunStatus.Stopped,
            runtime.SessionService.GetLatestCheckpoint(childSession.SessionId)!.Status
        );
        Assert.Equal(
            AgentRunStatus.Stopped,
            runtime.SessionService.GetLatestCheckpoint(grandchildSession.SessionId)!.Status
        );
        Assert.Equal(
            AgentRunStatus.Completed,
            runtime.SessionService.GetLatestCheckpoint(completedChildSession.SessionId)!.Status
        );
        Assert.Equal(
            AgentSessionState.Stopped,
            runtime.SessionService.GetSession(childSession.SessionId)!.State
        );
        Assert.Equal(
            AgentSessionState.Stopped,
            runtime.SessionService.GetSession(grandchildSession.SessionId)!.State
        );
    }
}
