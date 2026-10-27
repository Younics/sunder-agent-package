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
    public async Task AgentChatViewModel_PreservesDraftPerSession_WhenSwitchingSessions()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionA = await runtime.CreateSessionAsync(toolId);
        var sessionB = runtime.SessionService.CreateSession(
            "Test Session B",
            workspaceId: runtime.CurrentWorkspaceId
        ).SessionId;
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == sessionA
        );
        viewModel.DraftMessage = "draft for session A";

        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == sessionB
        );
        viewModel.DraftMessage = "draft for session B";

        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == sessionA
        );
        Assert.Equal("draft for session A", viewModel.DraftMessage);

        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == sessionB
        );
        Assert.Equal("draft for session B", viewModel.DraftMessage);
    }

    [Fact]
    public async Task AgentChatViewModel_MarksBackgroundSessionUnread_WithoutOverwritingSelectedSessionState()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionA = await runtime.CreateSessionAsync(toolId);
        var sessionB = runtime.SessionService.CreateSession(
            "Test Session B",
            workspaceId: runtime.CurrentWorkspaceId
        ).SessionId;
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        var sessionAItem = viewModel.Sessions.Single(session => session.SessionId == sessionA);
        var sessionBItem = viewModel.Sessions.Single(session => session.SessionId == sessionB);

        viewModel.SelectedSession = sessionAItem;
        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.DraftMessage = "keep selected draft";

        var selectedRevision = runtime.SessionService.GetNextRunRevision(sessionA);
        runtime.SessionService.SaveCheckpoint(
            sessionA,
            selectedRevision,
            AgentRunStatus.Completed,
            "Primary session completed."
        );

        Assert.Contains(
            "Primary session completed.",
            viewModel.StatusText,
            StringComparison.Ordinal
        );

        var backgroundRevision = runtime.SessionService.GetNextRunRevision(sessionB);
        runtime.SessionService.SaveCheckpoint(
            sessionB,
            backgroundRevision,
            AgentRunStatus.Running,
            "Background session is still running."
        );

        Assert.True(sessionBItem.HasUnreadActivity);
        Assert.Equal("keep selected draft", viewModel.DraftMessage);
        Assert.Contains(
            "Primary session completed.",
            viewModel.StatusText,
            StringComparison.Ordinal
        );

        viewModel.SelectedSession = sessionBItem;
        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(sessionBItem.HasUnreadActivity);
        Assert.Equal(string.Empty, viewModel.DraftMessage);
        Assert.Contains(
            "Background session is still running.",
            viewModel.StatusText,
            StringComparison.Ordinal
        );
    }

    [Fact]
    public async Task AgentChatViewModel_SendMessageCommand_AllowsIdleSessionSendWhileAnotherSessionRuns()
    {
        const string toolId = "fetch_page";
        const string firstMessage = "first background run";
        const string secondMessage = "second session run";

        var blockingTool = new BlockingTool(toolId);
        var provider = new ScriptedProvider(
            (request, _) =>
            {
                var hasToolResult = request.Turns.Any(turn =>
                    turn.Kind == AgentTurnKind.ToolResult
                );
                if (RequestContainsUserText(request, firstMessage))
                {
                    return hasToolResult
                        ? Complete("first done")
                        : ToolRequest("call-1", toolId, "{}");
                }

                if (RequestContainsUserText(request, secondMessage))
                {
                    return Complete("second done");
                }

                throw new Xunit.Sdk.XunitException("Unexpected provider request.");
            }
        );

        using var runtime = AgentTestRuntime.Create(provider, blockingTool);
        var sessionA = await runtime.CreateSessionAsync(toolId);
        var sessionB = runtime.SessionService.CreateSession(
            "Test Session B",
            workspaceId: runtime.CurrentWorkspaceId
        ).SessionId;
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == sessionA
        );
        viewModel.DraftMessage = firstMessage;
        var firstSendTask = viewModel.SendMessageCommand.ExecuteAsync(null);
        try
        {
            await blockingTool.Started.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(firstSendTask.IsCompleted);

            viewModel.DraftMessage = "duplicate same session";
            Assert.False(viewModel.SendMessageCommand.CanExecute(null));

            viewModel.SelectedSession = viewModel.Sessions.Single(session =>
                session.SessionId == sessionB
            );
            viewModel.DraftMessage = secondMessage;

            Assert.True(viewModel.IsSelectedSessionRunInactive);
            Assert.True(viewModel.SendMessageCommand.CanExecute(null));

            await viewModel.SendMessageCommand.ExecuteAsync(null).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(
                AgentRunStatus.Completed,
                runtime.SessionService.GetLatestCheckpoint(sessionB)?.Status
            );
            Assert.Contains(
                runtime.SessionService.ListTurns(sessionB),
                turn => turn.Role == AgentMessageRole.Assistant && RenderTurnText(turn) == "second done"
            );
        }
        finally
        {
            blockingTool.Release();
        }

        await firstSendTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(
            AgentRunStatus.Completed,
            runtime.SessionService.GetLatestCheckpoint(sessionA)?.Status
        );
        Assert.Contains(
            runtime.SessionService.ListTurns(sessionA),
            turn => turn.Role == AgentMessageRole.Assistant && RenderTurnText(turn) == "first done"
        );
    }

    [Fact]
    public async Task AgentChatViewModel_PreservesSelectedSession_WhenActivityReordersSessionList()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionA = await runtime.CreateSessionAsync(toolId);
        var sessionB = runtime.SessionService.CreateSession(
            "Test Session B",
            workspaceId: runtime.CurrentWorkspaceId
        ).SessionId;
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        var sessionAItem = viewModel.Sessions.Single(session => session.SessionId == sessionA);
        viewModel.SelectedSession = sessionAItem;
        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.DraftMessage = "keep selected draft";
        var observedMove = false;
        viewModel.Sessions.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Move)
            {
                return;
            }

            observedMove = true;
            viewModel.SelectedSession = null;
        };

        runtime.SessionService.SaveCheckpoint(
            sessionA,
            runtime.SessionService.GetNextRunRevision(sessionA),
            AgentRunStatus.Running,
            "Selected session is running."
        );

        Assert.True(observedMove);
        Assert.Equal(sessionA, viewModel.SelectedSession?.SessionId);
        Assert.Equal(sessionA, viewModel.DisplayedSession?.SessionId);
        Assert.Equal("keep selected draft", viewModel.DraftMessage);
        Assert.False(viewModel.ShowSetupInstructions);
        Assert.True(viewModel.ShowCollapsedComposer);
        Assert.Equal(
            0,
            viewModel.Sessions.IndexOf(
                viewModel.Sessions.Single(session => session.SessionId == sessionA)
            )
        );
        Assert.Contains(viewModel.Sessions, session => session.SessionId == sessionB);
    }

    [Fact]
    public async Task AgentChatViewModel_SendMessage_PreservesMarkdownWhitespaceExactly()
    {
        const string toolId = "fetch_page";
        const string markdown = "    indented code\n\n## Heading\n\n- item\n";
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator);
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == sessionId);
        viewModel.DraftMessage = markdown;

        await viewModel.SendMessageCommand.ExecuteAsync(null);

        var userTurn = runtime.SessionService.ListTurns(sessionId)
            .Single(turn => turn.Role == AgentMessageRole.User);
        Assert.Equal(markdown, Assert.Single(userTurn.Items).TextContent);
    }

    [Fact]
    public async Task AgentChatViewModel_LoadsRecentTranscriptWindowAndOlderRowsOnDemand()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 165; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.User,
                $"message-{index:000}"
            );
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        Assert.Equal(45, viewModel.Messages.Count);
        Assert.True(viewModel.HasOlderTranscriptRows);
        Assert.Equal(
            "message-120",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content
        );
        Assert.Equal(
            "message-164",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content
        );

        var loaded = await viewModel.LoadOlderTranscriptRowsAsync();

        Assert.True(loaded);
        Assert.Equal(60, viewModel.Messages.Count);
        Assert.True(viewModel.HasOlderTranscriptRows);
        Assert.True(viewModel.HasNewerTranscriptRows);
        Assert.Equal(
            "message-090",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content
        );
        Assert.Equal(
            "message-149",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content
        );
    }

    [Fact]
    public async Task AgentChatViewModel_PagingOlderAndNewerKeepsWindowCappedAndDirectional()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 260; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.User,
                $"message-{index:000}"
            );
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        Assert.Equal(45, viewModel.Messages.Count);
        Assert.Equal(
            "message-215",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content
        );
        Assert.Equal(
            "message-259",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content
        );

        Assert.True(await viewModel.LoadOlderTranscriptRowsAsync());

        Assert.Equal(60, viewModel.Messages.Count);
        Assert.True(viewModel.HasOlderTranscriptRows);
        Assert.True(viewModel.HasNewerTranscriptRows);
        Assert.Equal(
            "message-185",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content
        );
        Assert.Equal(
            "message-244",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content
        );

        Assert.True(await viewModel.LoadNewerTranscriptRowsAsync());

        Assert.Equal(60, viewModel.Messages.Count);
        Assert.True(viewModel.HasOlderTranscriptRows);
        Assert.False(viewModel.HasNewerTranscriptRows);
        Assert.Equal(
            "message-200",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content
        );
        Assert.Equal(
            "message-259",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content
        );
    }

    [Fact]
    public async Task AgentChatViewModel_LoadOlderTranscriptRows_PreservesProtectedAnchorKey()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 260; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.User,
                $"message-{index:000}"
            );
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        var protectedRow = viewModel.Messages
            .OfType<AgentTextTranscriptRowViewModel>()
            .Single(row => row.Content == "message-240");
        var protectedAnchorKey = protectedRow.AnchorKey;

        Assert.True(await viewModel.LoadOlderTranscriptRowsAsync(protectedAnchorKey));

        Assert.Equal(60, viewModel.Messages.Count);
        Assert.Contains(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "message-240" && Equals(row.AnchorKey, protectedAnchorKey)
        );
    }

    [Fact]
    public async Task AgentChatViewModel_RepeatedPagingOlderAndNewerNeverExceedsVisibleRowLimit()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 400; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.User,
                $"message-{index:000}"
            );
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        Assert.Equal(45, viewModel.Messages.Count);
        Assert.Equal("message-355", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content);
        Assert.Equal("message-399", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content);

        Assert.True(await viewModel.LoadOlderTranscriptRowsAsync());
        Assert.Equal(60, viewModel.Messages.Count);
        Assert.Equal("message-325", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content);
        Assert.Equal("message-384", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content);

        Assert.True(await viewModel.LoadOlderTranscriptRowsAsync());
        Assert.Equal(60, viewModel.Messages.Count);
        Assert.Equal("message-295", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content);
        Assert.Equal("message-354", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content);

        Assert.True(await viewModel.LoadNewerTranscriptRowsAsync());
        Assert.Equal(60, viewModel.Messages.Count);
        Assert.Equal("message-325", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content);
        Assert.Equal("message-384", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content);

        Assert.True(await viewModel.LoadNewerTranscriptRowsAsync());
        Assert.Equal(60, viewModel.Messages.Count);
        Assert.Equal("message-340", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[0]).Content);
        Assert.Equal("message-399", Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content);
        Assert.False(viewModel.HasNewerTranscriptRows);
    }

    [Fact]
    public async Task AgentChatViewModel_KeepsTranscriptWindowBounded_WhenPagingBothDirections()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 260; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.User,
                $"message-{index:000}"
            );
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        for (var index = 0; index < 10 && viewModel.CanLoadOlderTranscriptRows; index++)
        {
            await viewModel.LoadOlderTranscriptRowsAsync();
        }

        Assert.InRange(viewModel.Messages.Count, 1, 60);
        Assert.True(viewModel.HasNewerTranscriptRows);

        var loadedNewer = await viewModel.LoadNewerTranscriptRowsAsync();

        Assert.True(loadedNewer);
        Assert.InRange(viewModel.Messages.Count, 1, 60);
        Assert.True(viewModel.HasOlderTranscriptRows);
    }

    [Fact]
    public async Task AgentChatViewModel_BuffersLiveOverflowWithoutResettingRetainedRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        viewModel.IsDetailedTranscript = true; // Detailed retains its existing row-overflow policy.
        await viewModel.InitializeAsync();

        for (var index = 0; index < 240; index++)
        {
            runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, $"message-{index:000}");
        }

        var retainedRow = viewModel.Messages
            .OfType<AgentTextTranscriptRowViewModel>()
            .Single(row => row.Content == "message-200");
        var resetCount = 0;
        var removeCount = 0;
        viewModel.Messages.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset)
            {
                resetCount++;
            }
            else if (args.Action == NotifyCollectionChangedAction.Remove)
            {
                removeCount++;
            }
        };

        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "message-240");

        Assert.Equal(0, resetCount);
        Assert.Equal(0, removeCount);
        Assert.InRange(viewModel.Messages.Count, 61, 90);
        Assert.True(viewModel.HasOlderTranscriptRows);
        Assert.DoesNotContain(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "message-150"
        );
        Assert.Same(
            retainedRow,
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>().Single(row => row.Content == "message-200")
        );
    }

    [Fact]
    public async Task AgentChatViewModel_KeepsToolHeavyLiveTranscriptWithinOverflowLimit()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        viewModel.IsDetailedTranscript = true;
        await viewModel.InitializeAsync();

        for (var index = 0; index < 140; index++)
        {
            var callId = $"call-{index:000}";
            runtime.SessionService.AppendToolCallTurn(sessionId, AgentMessageRole.Assistant, callId, toolId, "{}");
            runtime.SessionService.AppendToolResultTurn(
                sessionId,
                callId,
                toolId,
                "{}",
                $"done-{index:000}",
                $"done-{index:000}",
                structuredPayloadJson: null,
                sourcesJson: null,
                wasTruncated: false,
                isError: false,
                errorCode: null,
                backendId: null);
        }

        Assert.InRange(viewModel.Messages.Count, 60, 90);
        Assert.True(viewModel.HasOlderTranscriptRows);
        var tools = viewModel.Messages.OfType<AgentToolInvocationRowViewModel>().ToArray();
        Assert.Equal(tools.Length, tools.Select(row => row.AnchorKey).Distinct().Count());
        Assert.All(tools, row =>
        {
            Assert.True(row.IsCompleted); // Re-paged invocation headers need no eager result receipt/payload.
            Assert.Equal(2, row.DetailRevision);
            Assert.False(row.HasMaterializedDetails);
        });
    }

    [Fact]
    public async Task AgentChatViewModel_DoesNotAppendUnboundedLiveRows_WhenBrowsingHistoricalWindow()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 320; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.User,
                $"message-{index:000}"
            );
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        for (var index = 0; index < 7 && viewModel.CanLoadOlderTranscriptRows; index++)
        {
            await viewModel.LoadOlderTranscriptRowsAsync();
        }
        var messageCount = viewModel.Messages.Count;

        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.Assistant, "new live message");

        Assert.Equal(messageCount, viewModel.Messages.Count);
        Assert.True(viewModel.HasNewerTranscriptRows);
    }

    [Fact]
    public async Task AgentChatViewModel_DetachedTranscriptBuffersLiveRowsUntilNewerRowsLoad()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "initial message");
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        var initialRow = Assert.Single(viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>());

        viewModel.DetachTranscriptFromLatest();
        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "live while detached"
        );

        Assert.Same(
            initialRow,
            Assert.Single(viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>())
        );
        Assert.True(viewModel.HasNewerTranscriptRows);
        Assert.DoesNotContain(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "live while detached"
        );

        var loaded = await viewModel.LoadNewerTranscriptRowsAsync();

        Assert.True(loaded);
        Assert.False(viewModel.HasNewerTranscriptRows);
        Assert.Contains(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "live while detached"
        );

        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "live after resume"
        );

        Assert.Contains(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "live after resume"
        );
    }

    [Fact]
    public async Task AgentChatViewModel_AcceptedSendRequestsTailFollowBeforeProjectingResponse()
    {
        const string toolId = "fetch_page";
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("followed response")),
            new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "initial message");
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.DetachTranscriptFromLatest());
        var requestedSessionIds = new List<Guid>();
        viewModel.TranscriptTailFollowRequested += requestedSessionIds.Add;
        viewModel.DraftMessage = "new message";

        await viewModel.SendMessageCommand.ExecuteAsync(null);

        Assert.Equal([sessionId], requestedSessionIds);
        Assert.True(viewModel.IsTranscriptFollowingLatest);
        Assert.False(viewModel.HasNewerTranscriptRows);
        Assert.Contains(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "new message");
        Assert.Contains(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "followed response");
    }

    [Fact]
    public async Task AgentChatViewModel_RejectedSendPreservesDraftAfterExplicitFollowIntent()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("unused")));
        var sessionId = await runtime.CreateSessionAsync("noop");
        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "initial message");
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            ThrowingRunGateway.Instance);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.DetachTranscriptFromLatest());
        viewModel.DraftMessage = "rejected message";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.SendMessageCommand.ExecuteAsync(null));
        await WaitUntilAsync(() => viewModel.SendMessageCommand.CanExecute(null));

        Assert.True(viewModel.IsTranscriptFollowingLatest);
        Assert.Equal("rejected message", viewModel.DraftMessage);
        Assert.True(viewModel.SendMessageCommand.CanExecute(null));
    }

    [Fact]
    public async Task AgentChatViewModel_UncertainSendRetriesReconciliationWithoutBlockingSession()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("unused")));
        var sessionId = await runtime.CreateSessionAsync("noop");
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            ThrowingRunGateway.Instance);
        await viewModel.InitializeAsync();
        var transcriptGateway = new FailOnceTranscriptPageGateway(runtime.SessionService);
        typeof(AgentChatViewModel)
            .GetField("_transcriptPageGateway", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(viewModel, transcriptGateway);
        viewModel.DraftMessage = "uncertain message";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.SendMessageCommand.ExecuteAsync(null));
        await WaitUntilAsync(() => viewModel.SendMessageCommand.CanExecute(null));

        Assert.True(transcriptGateway.InvocationCount >= 2);
        Assert.Equal("uncertain message", viewModel.DraftMessage);
    }

    [Fact]
    public async Task AgentChatViewModel_LostResponseWhileAdmissionPendingDoesNotRestoreOrResend()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("unused")));
        var sessionId = await runtime.CreateSessionAsync("noop");
        var gateway = new PendingAdmissionRunGateway(runtime.Store);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            gateway);
        await viewModel.InitializeAsync();
        viewModel.DraftMessage = "admit exactly once";

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => viewModel.SendMessageCommand.ExecuteAsync(null));
        await gateway.StatusChecked.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Assert.Equal(1, gateway.CommandCount);
        Assert.Equal("admit exactly once", viewModel.DraftMessage);
        Assert.False(viewModel.SendMessageCommand.CanExecute(null));

        gateway.CommitPendingAdmission();
        await WaitUntilAsync(() => string.IsNullOrEmpty(viewModel.DraftMessage));
        viewModel.DraftMessage = "next message";

        Assert.Equal(1, gateway.CommandCount);
        Assert.True(viewModel.SendMessageCommand.CanExecute(null));
        Assert.Equal(sessionId, gateway.SessionId);
    }

    [Fact]
    public async Task AgentChatViewModel_SendClearsComposerAfterAuthoritativeUserRowIsProjected()
    {
        const string toolId = "fetch_page";
        const string message = "atomic composer transition";
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId));
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator);
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sessions.Single(session => session.SessionId == sessionId);
        viewModel.DraftMessage = message;
        var userRowWasPresentWhenComposerCleared = false;
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentChatViewModel.DraftMessage)
                && string.IsNullOrEmpty(viewModel.DraftMessage))
            {
                userRowWasPresentWhenComposerCleared = viewModel.Messages
                    .OfType<AgentTextTranscriptRowViewModel>()
                    .Any(row => row.IsUser && row.Content == message);
            }
        };

        await viewModel.SendMessageCommand.ExecuteAsync(null);

        Assert.True(userRowWasPresentWhenComposerCleared);
        Assert.Empty(viewModel.DraftMessage);
    }

    [Fact]
    public async Task AgentChatViewModel_ExactFallbackDoesNotAppendOffWindowTurnAtTail()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")));
        var sessionId = await runtime.CreateSessionAsync("noop");
        var oldTurn = runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.User,
            "old correlated request");
        for (var index = 0; index < 80; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.Assistant,
                $"newer response {index:00}");
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator);
        await viewModel.InitializeAsync();
        Assert.DoesNotContain(viewModel.Messages, row => row.RowId == oldTurn.TurnId);
        var composer = Assert.IsType<AgentComposerState>(
            typeof(AgentChatViewModel)
                .GetField("_composer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel));
        var submission = Assert.IsType<AgentComposerSubmission>(
            composer.TryBeginSubmission(sessionId));
        typeof(AgentComposerSubmission)
            .GetField("<UserTurnId>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(submission, oldTurn.TurnId);
        var completeSubmission = typeof(AgentChatViewModel).GetMethod(
            "CompleteOrRestoreComposerSubmissionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        await Assert.IsAssignableFrom<Task<bool>>(completeSubmission.Invoke(
            viewModel,
            [viewModel.SelectedSession!, submission, true]));

        Assert.True(submission.IsCommitted);
        Assert.DoesNotContain(viewModel.Messages, row => row.RowId == oldTurn.TurnId);
        await WaitUntilAsync(() => viewModel.StatusText != "Loading transcript...");
        Assert.DoesNotContain(viewModel.Messages, row => row.RowId == oldTurn.TurnId);
    }

    [Fact]
    public async Task AgentChatViewModel_JumpToLatestReloadsDetachedTranscriptAndResumesLiveRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        for (var index = 0; index < 130; index++)
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.User,
                $"message-{index:000}"
            );
        }

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        viewModel.DetachTranscriptFromLatest();
        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "live while detached"
        );

        Assert.DoesNotContain(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "live while detached"
        );
        Assert.True(viewModel.HasNewerTranscriptRows);

        viewModel.JumpToLatestTranscriptCommand.Execute(null);

        await WaitUntilAsync(() => !viewModel.HasNewerTranscriptRows);
        Assert.False(viewModel.HasNewerTranscriptRows);
        Assert.Equal(
            "live while detached",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content
        );

        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "live after jump"
        );

        Assert.Equal(
            "live after jump",
            Assert.IsType<AgentTextTranscriptRowViewModel>(viewModel.Messages[^1]).Content
        );
    }

    [Fact]
    public async Task AgentChatViewModel_UpdatesStreamingAssistantTurnInPlace()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        var lease = new AgentDurableRunLease(runtime.Store.ReserveRun(sessionId, runtime.CurrentProfileId, "partial"));
        Assert.NotNull(runtime.SessionService.TryTransitionRun(lease, AgentRunStatus.Running, "Responding."));
        var resets = 0;
        runtime.SessionService.TranscriptReset += _ => resets++;
        var turn = runtime.SessionService.AppendTextTurn(
            lease,
            AgentMessageRole.Assistant,
            "partial"
        );
        var row = Assert.IsType<AgentTextTranscriptRowViewModel>(Assert.Single(viewModel.Messages));
        var markdownBuilder = row.MarkdownBuilder;

        runtime.SessionService.UpdateTextTurn(lease, turn.TurnId, "partial plus more");

        var updatedRow = Assert.IsType<AgentTextTranscriptRowViewModel>(
            Assert.Single(viewModel.Messages)
        );
        Assert.Same(row, updatedRow);
        Assert.Same(markdownBuilder, updatedRow.MarkdownBuilder);
        Assert.Equal("partial plus more", updatedRow.Content);
        Assert.Equal(0, resets);
    }

    [Fact]
    public void AgentTextTranscriptRowViewModel_PreservesMarkdownBuilder_ForNonPrefixUpdate()
    {
        var now = DateTimeOffset.UtcNow;
        var turn = new AgentTurnRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [
                new AgentTurnItemRecord(
                    Guid.NewGuid(),
                    Guid.Empty,
                    0,
                    AgentTurnItemKind.Text,
                    "first",
                    null,
                    null,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    null,
                    null),
            ],
            now,
            now);
        var row = new AgentTextTranscriptRowViewModel(ToAppTurn(turn), "first");
        var markdownBuilder = row.MarkdownBuilder;

        row.UpdateContent("replacement");

        Assert.Same(markdownBuilder, row.MarkdownBuilder);
        Assert.Equal("replacement", row.MarkdownBuilder.ToString());
        Assert.Equal("replacement", row.Content);
    }

    [Fact]
    public void AgentTextTranscriptRowViewModel_FormatsAssistantMessageHeaderInLocalTime()
    {
        var localCreatedAt = DateTime.Today.AddHours(14).AddMinutes(35);
        var createdAt = new DateTimeOffset(
            localCreatedAt,
            TimeZoneInfo.Local.GetUtcOffset(localCreatedAt)).ToUniversalTime();
        var turn = new AgentTurnRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AgentMessageRole.Assistant,
            AgentTurnKind.Message,
            [],
            createdAt,
            createdAt);

        var row = new AgentTextTranscriptRowViewModel(
            ToAppTurn(turn),
            "hello",
            senderDisplayName: "Package Developer");

        Assert.True(row.ShowMessageHeader);
        Assert.Equal("Package Developer", row.SenderDisplayName);
        Assert.Contains("Today", row.SentAtText, StringComparison.Ordinal);
        Assert.Contains(
            createdAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            row.SentAtText,
            StringComparison.Ordinal);
        Assert.Equal($"Package Developer · {row.SentAtText}", row.MessageHeaderText);
    }

    [Fact]
    public void AgentTextTranscriptRowViewModel_FormatsUserMessageHeaderInLocalTime()
    {
        var localCreatedAt = DateTime.Today.AddHours(9).AddMinutes(5);
        var createdAt = new DateTimeOffset(
            localCreatedAt,
            TimeZoneInfo.Local.GetUtcOffset(localCreatedAt)).ToUniversalTime();
        var turn = new AgentTurnRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            AgentMessageRole.User,
            AgentTurnKind.Message,
            [],
            createdAt,
            createdAt);

        var row = new AgentTextTranscriptRowViewModel(
            ToAppTurn(turn),
            "hello",
            senderDisplayName: "Ignored Agent Name");

        Assert.True(row.ShowMessageHeader);
        Assert.Equal("You", row.SenderDisplayName);
        Assert.Contains("Today", row.SentAtText, StringComparison.Ordinal);
        Assert.Contains(
            createdAt.ToLocalTime().ToString("t", CultureInfo.CurrentCulture),
            row.SentAtText,
            StringComparison.Ordinal);
        Assert.Equal($"You · {row.SentAtText}", row.MessageHeaderText);
    }

    [Fact]
    public async Task AgentChatViewModel_UsesProfileNameForAssistantMessageHeader()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var sessionId = await runtime.CreateSessionAsync("noop");
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "response"
        );

        var row = Assert.IsType<AgentTextTranscriptRowViewModel>(Assert.Single(viewModel.Messages));
        Assert.Equal("Test Profile", row.SenderDisplayName);
        Assert.True(row.ShowMessageHeader);
    }

    [Fact]
    public async Task AgentChatViewModel_WorkspaceSave_PreservesSelectedSessionAndTranscriptRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "existing response"
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        Assert.NotNull(viewModel.SelectedSession);
        var selectedSession = viewModel.SelectedSession;
        var transcriptRow = Assert.Single(viewModel.Messages);
        var workspaceId = viewModel.SelectedWorkspace!.WorkspaceId;
        var transcriptChangedCount = 0;
        viewModel.Workspaces.CollectionChanged += (_, args) =>
        {
            if (
                args.Action
                is NotifyCollectionChangedAction.Replace
                    or NotifyCollectionChangedAction.Remove
                    or NotifyCollectionChangedAction.Reset
            )
            {
                viewModel.SelectedWorkspace = null;
            }
        };
        viewModel.TranscriptChanged += () => transcriptChangedCount++;

        runtime.WorkspaceService.SaveWorkspace(
            workspaceId,
            "Renamed Workspace",
            "Updated description"
        );
        await WaitUntilAsync(() => viewModel.SelectedWorkspace?.DisplayName == "Renamed Workspace");

        Assert.Same(selectedSession, viewModel.SelectedSession);
        Assert.Same(selectedSession, Assert.Single(viewModel.Sessions));
        Assert.Same(transcriptRow, Assert.Single(viewModel.Messages));
        Assert.Equal(0, transcriptChangedCount);
        Assert.Equal("Renamed Workspace", viewModel.SelectedWorkspace?.DisplayName);
        Assert.Contains(
            viewModel.Workspaces,
            workspace =>
                string.Equals(
                    workspace.WorkspaceId,
                    workspaceId,
                    StringComparison.OrdinalIgnoreCase
                )
                && workspace.DisplayName == "Renamed Workspace"
        );
    }

    [Fact]
    public async Task AgentChatViewModel_SendMessageCommand_PreservesExistingExpandedToolRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendToolCallTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "call-1",
            toolId,
            "{\"url\":\"https://example.com\"}"
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        await RevealCompactGroupsAsync(viewModel);
        var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        var expandedDetails = await TranscriptToolTestHarness.ExpandAsync(toolRow);

        viewModel.DraftMessage = "continue";
        await viewModel.SendMessageCommand.ExecuteAsync(null);

        var preservedToolRow = Assert.Single(
            viewModel.Messages.OfType<AgentToolInvocationRowViewModel>()
        );
        Assert.Same(toolRow, preservedToolRow);
        Assert.True(preservedToolRow.IsExpanded);
        Assert.Same(expandedDetails, preservedToolRow.ExpandedDetails);
        Assert.Contains(
            viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>(),
            row => row.Content == "done"
        );
    }

    [Fact]
    public async Task AgentChatViewModel_StopRunCommand_PreservesExistingExpandedToolRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        runtime.SessionService.AppendToolCallTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "call-1",
            toolId,
            "{\"url\":\"https://example.com\"}"
        );
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runtime.SessionService.GetNextRunRevision(sessionId),
            AgentRunStatus.Running,
            "Thinking."
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        await RevealCompactGroupsAsync(viewModel);
        var toolRow = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        var expandedDetails = await TranscriptToolTestHarness.ExpandAsync(toolRow);

        await viewModel.StopRunCommand.ExecuteAsync(null);

        var preservedToolRow = Assert.Single(
            viewModel.Messages.OfType<AgentToolInvocationRowViewModel>()
        );
        Assert.Same(toolRow, preservedToolRow);
        Assert.True(preservedToolRow.IsExpanded);
        Assert.Same(expandedDetails, preservedToolRow.ExpandedDetails);
        Assert.False(viewModel.RunActivityRow.IsVisible);
    }

    [Fact]
    public async Task AgentChatViewModel_ShowsActivityRowOnlyWhileSelectedSessionRuns()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runtime.SessionService.GetNextRunRevision(sessionId),
            AgentRunStatus.Running,
            "Thinking."
        );

        var activityRow = viewModel.RunActivityRow;
        Assert.True(activityRow.IsVisible);
        Assert.Empty(viewModel.Messages);
        Assert.StartsWith("Thinking", activityRow.ThinkingText, StringComparison.Ordinal);

        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runtime.SessionService.GetNextRunRevision(sessionId),
            AgentRunStatus.Completed,
            "Done."
        );

        Assert.Empty(viewModel.Messages);
        Assert.False(activityRow.IsVisible);
    }

    [Fact]
    public async Task AgentChatViewModel_SessionReplacementRebuildsRunActivityState()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("done")));
        var firstSessionId = await runtime.CreateSessionAsync("noop");
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator);
        await viewModel.InitializeAsync();
        var firstRunRevision = runtime.SessionService.GetNextRunRevision(firstSessionId);
        runtime.SessionService.SaveCheckpoint(
            firstSessionId,
            firstRunRevision,
            AgentRunStatus.Running,
            "Thinking.");
        runtime.SessionService.AppendTextTurn(
            firstSessionId,
            AgentMessageRole.Assistant,
            "partial response");
        Assert.True(viewModel.RunActivityRow.IsVisible);

        typeof(AgentChatViewModel)
            .GetMethod(
                "RefreshTranscript",
                BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(viewModel, [true]);
        await WaitUntilAsync(() => !viewModel.IsTranscriptLoading);
        Assert.True(viewModel.RunActivityRow.IsVisible);

        var secondSession = runtime.SessionService.CreateSession(
            "Second session",
            workspaceId: runtime.CurrentWorkspaceId);
        runtime.SessionService.SaveCheckpoint(
            secondSession.SessionId,
            runtime.SessionService.GetNextRunRevision(secondSession.SessionId),
            AgentRunStatus.Running,
            "Thinking.");
        viewModel.SelectedSession = viewModel.Sessions.Single(
            session => session.SessionId == secondSession.SessionId);

        await WaitUntilAsync(() =>
            viewModel.DisplayedSession?.SessionId == secondSession.SessionId
            && viewModel.RunActivityRow.IsVisible);
        Assert.Empty(viewModel.Messages);
    }

    [Fact]
    public async Task AgentChatViewModel_ShowsLiveReasoningActivity_WhenProviderReportsIt()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking."
        );
        runtime.SessionService.ReportRunActivity(
            sessionId,
            runRevision,
            AgentRunActivityKind.Reasoning,
            "Checking the latest transcript before choosing a tool."
        );

        var activityRow = viewModel.RunActivityRow;
        Assert.True(activityRow.IsVisible);
        Assert.Empty(viewModel.Messages);
        Assert.StartsWith("Thinking", activityRow.ThinkingText);
        Assert.Equal("Checking the latest transcript before choosing a tool.", activityRow.ReasoningText);
    }

    [Fact]
    public async Task AgentChatViewModel_KeepsActivityRowAfterStreamingAssistantText()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking."
        );

        var activityRow = viewModel.RunActivityRow;
        Assert.True(activityRow.IsVisible);
        Assert.Empty(viewModel.Messages);

        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "partial response"
        );
        runtime.SessionService.ReportRunActivity(sessionId, runRevision, AgentRunActivityKind.Responding, "Responding");

        var textRow = Assert.IsType<AgentTextTranscriptRowViewModel>(
            Assert.Single(viewModel.Messages)
        );
        Assert.Equal("partial response", textRow.Content);
        Assert.True(activityRow.IsVisible);
        Assert.Same(activityRow, viewModel.TranscriptItems[^2]);
        Assert.StartsWith("Responding", activityRow.ThinkingText);
    }

    [Fact]
    public async Task AgentChatViewModel_KeepsActivityRowAfterToolActivityStarts()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runtime.SessionService.GetNextRunRevision(sessionId),
            AgentRunStatus.Running,
            "Thinking."
        );

        var activityRow = viewModel.RunActivityRow;
        Assert.True(activityRow.IsVisible);
        Assert.Empty(viewModel.Messages);

        runtime.SessionService.AppendToolCallTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "call-1",
            toolId,
            "{\"url\":\"https://example.com\"}"
        );

        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.IsType<AgentActivityGroupRowViewModel>(Assert.Single(viewModel.Messages)).IsRunning);
        Assert.True(activityRow.IsVisible);
        Assert.Same(activityRow, viewModel.TranscriptItems[^2]);
    }

    [Fact]
    public async Task AgentChatViewModel_KeepsActivityWhileToolRunsAndReviewsResult()
    {
        var toolId = McpToolIdentity.CreateStable("stitch-server", "edit_screens");

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking."
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            activityQuietDelay: TimeSpan.Zero
        );
        await viewModel.InitializeAsync();

        var activityRow = viewModel.RunActivityRow;
        Assert.True(activityRow.IsVisible);
        Assert.Empty(viewModel.Messages);

        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Running,
            $"Executing tool '{toolId}'."
        );
        runtime.SessionService.AppendToolCallTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "call-1",
            toolId,
            "{\"url\":\"https://example.com\"}"
        );

        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(Assert.IsType<AgentActivityGroupRowViewModel>(Assert.Single(viewModel.Messages)).IsRunning);
        Assert.True(activityRow.IsVisible);
        Assert.Contains("Edit Screens", activityRow.ThinkingText, StringComparison.Ordinal);
        Assert.DoesNotContain("m1_", activityRow.ThinkingText, StringComparison.OrdinalIgnoreCase);

        runtime.SessionService.AppendToolResultTurn(
            sessionId,
            "call-1",
            toolId,
            "{\"url\":\"https://example.com\"}",
            "completed",
            "completed",
            structuredPayloadJson: null,
            sourcesJson: null,
            wasTruncated: false,
            isError: false,
            errorCode: null,
            backendId: null);
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Running,
            $"Tool '{toolId}' completed. Continuing provider execution.");
        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(activityRow.IsVisible);
        Assert.StartsWith("Reviewing result", activityRow.ThinkingText, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentActivityTranscriptRowViewModel_KeepsFallbackLightweight()
    {
        using var activityRow = new AgentActivityTranscriptRowViewModel("Processing result");

        Assert.StartsWith("Reviewing result", activityRow.ThinkingText, StringComparison.Ordinal);
    }

    [Fact]
    public void AgentActivityTranscriptRowViewModel_StripsReasoningMarkdown()
    {
        using var activityRow = new AgentActivityTranscriptRowViewModel(
            "**Fetching New York weather** I need the current weather for New York City.",
            isReasoningActivity: true
        );

        Assert.Equal(
            "Fetching New York weather I need the current weather for New York City. ...",
            activityRow.ThinkingText
        );
    }

    [Fact]
    public void AgentActivityTranscriptRowViewModel_PreservesReasoningLines()
    {
        using var activityRow = new AgentActivityTranscriptRowViewModel(
            "**First step.**\n- Second step.\n3. Third step.",
            isReasoningActivity: true
        );

        Assert.Equal(
            string.Join(Environment.NewLine, "First step.", "Second step.", "Third step.") + " ...",
            activityRow.ThinkingText
        );
    }

    [Fact]
    public void AgentActivityTranscriptRowViewModel_DoesNotPreclipReasoningText()
    {
        var reasoningText = "I need to fetch the New York forecast. "
                            + "I will use the coordinates for NYC, which are 40.7128, -74.0060. "
                            + string.Join(' ', Enumerable.Repeat("Additional planning context follows.", 20));
        using var activityRow = new AgentActivityTranscriptRowViewModel(
            reasoningText,
            isReasoningActivity: true
        );

        Assert.Equal(reasoningText + " ...", activityRow.ThinkingText);
    }

    [Fact]
    public async Task AgentChatViewModel_CompletionPreventsQuietActivityRowReturning()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking."
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            activityQuietDelay: TimeSpan.Zero
        );
        await viewModel.InitializeAsync();

        runtime.SessionService.AppendToolCallTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "call-1",
            toolId,
            "{\"url\":\"https://example.com\"}"
        );
        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(viewModel.RunActivityRow.IsVisible);

        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Completed,
            "Done."
        );

        Assert.False(viewModel.RunActivityRow.IsVisible);
        Assert.Single(viewModel.Messages.OfType<AgentActivityGroupRowViewModel>());
    }

    [Fact]
    public async Task AgentChatViewModel_NewUserTurnResetsActivityRowForNextRun()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var sessionId = await runtime.CreateSessionAsync(toolId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        var runRevision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking."
        );
        runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "previous response"
        );
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runRevision,
            AgentRunStatus.Completed,
            "Done."
        );

        Assert.False(viewModel.RunActivityRow.IsVisible);

        runtime.SessionService.AppendTextTurn(sessionId, AgentMessageRole.User, "next request");
        runtime.SessionService.SaveCheckpoint(
            sessionId,
            runtime.SessionService.GetNextRunRevision(sessionId),
            AgentRunStatus.Running,
            "Thinking again."
        );

        Assert.True(viewModel.RunActivityRow.IsVisible);
    }

    [Fact]
    public async Task AgentChatViewModel_BackgroundSubmissionDoesNotResetDisplayedRunActivity()
    {
        var blockNextReadiness = 0;
        var readinessStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseReadiness = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);

        async ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken)
        {
            if (Interlocked.Exchange(ref blockNextReadiness, 0) != 0)
            {
                readinessStarted.TrySetResult();
                await releaseReadiness.Task.WaitAsync(cancellationToken);
            }

            return new AgentProviderReadiness(
                "test-provider",
                AgentProviderReadinessStatus.Ready,
                "Ready.");
        }

        using var runtime = AgentTestRuntime.Create(new ScriptedProvider(
            (_, _) => Complete("done"),
            readinessHandler: GetReadinessAsync));
        var displayedSessionId = await runtime.CreateSessionAsync("noop");
        var backgroundSessionId = runtime.SessionService.CreateSession(
            "Background session",
            workspaceId: runtime.CurrentWorkspaceId).SessionId;
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            activityQuietDelay: TimeSpan.FromHours(1));
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sessions.Single(
            session => session.SessionId == backgroundSessionId);
        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        viewModel.DraftMessage = "background request";

        Volatile.Write(ref blockNextReadiness, 1);
        var sendTask = viewModel.SendMessageCommand.ExecuteAsync(null);
        await readinessStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        viewModel.SelectedSession = viewModel.Sessions.Single(
            session => session.SessionId == displayedSessionId);
        await viewModel.PendingTranscriptActivities.WaitAsync(TimeSpan.FromSeconds(5));
        var displayedRunRevision = runtime.SessionService.GetNextRunRevision(displayedSessionId);
        runtime.SessionService.SaveCheckpoint(
            displayedSessionId,
            displayedRunRevision,
            AgentRunStatus.Running,
            "Thinking.");
        runtime.SessionService.AppendTextTurn(
            displayedSessionId,
            AgentMessageRole.Assistant,
            "visible response");
        Assert.True(viewModel.RunActivityRow.IsVisible);

        releaseReadiness.TrySetResult();
        await sendTask.WaitAsync(TimeSpan.FromSeconds(10));
        await WaitUntilAsync(() => runtime.SessionService.GetLatestCheckpoint(backgroundSessionId)?.Status == AgentRunStatus.Completed);

        Assert.Equal(displayedSessionId, viewModel.DisplayedTranscriptSessionId);
        Assert.Empty(viewModel.Sessions.Single(
            session => session.SessionId == backgroundSessionId).DraftMessage);
        var draftCache = Assert.IsType<Dictionary<Guid, string>>(
            typeof(AgentChatViewModel)
                .GetField("_sessionDrafts", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .GetValue(viewModel));
        Assert.True(!draftCache.TryGetValue(backgroundSessionId, out var cachedDraft) || string.IsNullOrEmpty(cachedDraft),
            $"A completed background submission must not retain sent text: '{cachedDraft}'.");
        Assert.True(viewModel.RunActivityRow.IsVisible);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranscriptRowsAndToolPresentation_AreEquivalentAcrossMainAndSubsessionFacades(bool detailed)
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var rootSessionId = await runtime.CreateSessionAsync("noop");
        var root = runtime.SessionService.GetSession(rootSessionId)!;
        var child = runtime.SessionService.CreateSession(
            "Child parity session",
            parentSessionId: root.SessionId,
            rootSessionId: root.SessionId,
            profileId: runtime.CurrentProfileId,
            workspaceId: runtime.CurrentWorkspaceId,
            agentKind: "subagent"
        );
        foreach (var sessionId in new[] { root.SessionId, child.SessionId })
        {
            runtime.SessionService.AppendTextTurn(
                sessionId,
                AgentMessageRole.Assistant,
                "Shared response."
            );
            runtime.SessionService.AppendToolCallTurn(
                sessionId,
                AgentMessageRole.Assistant,
                "parity-call",
                "read_file",
                "{\"path\":\"README.md\"}"
            );
            runtime.SessionService.AppendToolResultTurn(
                sessionId,
                "parity-call",
                "read_file",
                "{\"path\":\"README.md\"}",
                "Read complete.",
                "file contents",
                structuredPayloadJson: null,
                sourcesJson: null,
                wasTruncated: false,
                isError: false,
                errorCode: null,
                backendId: null
            );
        }

        using var main = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        main.IsDetailedTranscript = detailed;
        await main.InitializeAsync();
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var subsessions = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter);
        subsessions.IsDetailedTranscript = detailed;
        await subsessions.OnNavigatedToAsync(
            new PackageViewNavigationContext(
                SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?>
                {
                    [SubagentConstants.SubsessionNavigationSessionIdKey] = child.SessionId.ToString("D"),
                }
            )
        );

        await RevealCompactGroupsAsync(main);
        await RevealCompactGroupsAsync(subsessions);
        await WaitUntilAsync(() => subsessions.Messages.OfType<SubsessionTextTranscriptRowViewModel>()
            .Any(row => row.Content == "Shared response."));
        var mainText = main.Messages.OfType<AgentTextTranscriptRowViewModel>()
            .Single(row => row.Content == "Shared response.");
        var childText = subsessions.Messages.OfType<SubsessionTextTranscriptRowViewModel>()
            .Single(row => row.Content == "Shared response.");
        Assert.Equal(mainText.Role.ToString(), childText.Role.ToString());
        Assert.Equal(mainText.RoleGlyph, childText.RoleGlyph);
        Assert.Equal(mainText.Content, childText.Content);

        var mainTool = Assert.Single(main.Messages.OfType<AgentToolInvocationRowViewModel>());
        var childTool = Assert.Single(
            subsessions.Messages.OfType<SubsessionToolInvocationRowViewModel>()
        );
        Assert.Equal(mainTool.ToolLabel, childTool.ToolLabel);
        Assert.Equal(mainTool.HeaderDetailText, childTool.HeaderDetailText);
        Assert.Equal(mainTool.StatusText, childTool.StatusText);
        var mainDetails = await TranscriptToolTestHarness.ExpandAsync(mainTool);
        var childDetails = await TranscriptToolTestHarness.ExpandAsync(childTool);
        Assert.Equal(mainDetails.OutputText, childDetails.OutputText);
    }
}
