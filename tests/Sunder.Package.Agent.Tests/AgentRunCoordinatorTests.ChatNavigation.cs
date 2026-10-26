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
    public async Task AgentChatViewModel_ShowsSetupState_WhenNoProfileExists()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        Assert.Empty(viewModel.Profiles);
        Assert.Null(viewModel.SelectedWorkspace);
        Assert.Null(viewModel.SelectedProfile);
        Assert.Null(viewModel.SelectedSession);
        Assert.True(viewModel.HasNoProfiles);
        Assert.False(viewModel.CanUseChat);
        Assert.True(viewModel.CannotUseChat);
        Assert.True(viewModel.ShowSetupInstructions);
        Assert.True(viewModel.ShowTranscriptSurface);
        Assert.False(viewModel.ShowCollapsedComposer);
        Assert.False(viewModel.ShowExpandedComposer);
        Assert.False(viewModel.IsSelectedSessionRunInactive);
        Assert.Equal("Create an agent before chatting", viewModel.SetupTitle);
    }

    [Fact]
    public async Task AgentChatViewModel_SelectionSupersedesPendingHistoryAnchorLoad()
    {
        const string toolId = "fetch_page";
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId));
        var firstSessionId = await runtime.CreateSessionAsync(toolId);
        var firstTurn = runtime.SessionService.AppendTextTurn(
            firstSessionId,
            AgentMessageRole.Assistant,
            "First history target.");
        var secondSession = runtime.SessionService.CreateSession(
            "Second session",
            workspaceId: runtime.CurrentWorkspaceId);
        var secondTurn = runtime.SessionService.AppendTextTurn(
            secondSession.SessionId,
            AgentMessageRole.Assistant,
            "Second session remains selected.");
        var initiallySelectedSession = runtime.SessionService.CreateSession(
            "Initially selected session",
            workspaceId: runtime.CurrentWorkspaceId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator);
        await viewModel.InitializeAsync();
        Assert.Equal(initiallySelectedSession.SessionId, viewModel.SelectedSession?.SessionId);
        var anchorGateway = new BlockingTranscriptAnchorGateway(firstTurn);
        viewModel.SetTranscriptAnchorGateway(anchorGateway);
        var navigation = viewModel.NavigateToHistoryAnchorAsync(
            new AgentCore::Sunder.Package.Agent.HistorySearch.HistorySearchNavigationTarget(
                runtime.CurrentWorkspaceId,
                firstSessionId,
                firstTurn.TurnId,
                Assert.Single(firstTurn.Items).ItemId,
                null,
                AgentCore::Sunder.Package.Agent.HistorySearch.HistoryAnchorKind.Text,
                firstTurn.CreatedAtUtc),
            CancellationToken.None);
        await anchorGateway.LoadStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var secondTranscriptApplied = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveSecondTranscript()
        {
            if (viewModel.DisplayedTranscriptSessionId == secondSession.SessionId)
            {
                secondTranscriptApplied.TrySetResult();
            }
        }
        viewModel.TranscriptChanged += ObserveSecondTranscript;
        try
        {
            viewModel.SelectedSession = Assert.Single(
                viewModel.Sessions,
                session => session.SessionId == secondSession.SessionId);
            ObserveSecondTranscript();
            await secondTranscriptApplied.Task.WaitAsync(TimeSpan.FromSeconds(2));
            anchorGateway.ReleaseLoad.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => navigation);
        }
        finally
        {
            anchorGateway.ReleaseLoad.TrySetResult();
            viewModel.TranscriptChanged -= ObserveSecondTranscript;
        }

        Assert.Equal(secondSession.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal(secondSession.SessionId, viewModel.DisplayedTranscriptSessionId);
        Assert.Contains(viewModel.Messages, row => row.RowId == secondTurn.TurnId);
        Assert.DoesNotContain(viewModel.Messages, row => row.RowId == firstTurn.TurnId);
    }

    [Fact]
    public async Task AgentChatViewModel_StartRollback_PopulatesComposerAndRestoresAttachments()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var sessionId = await runtime.CreateSessionAsync("noop");
        var storedAttachment = await runtime.AttachmentService.StoreAttachmentAsync(
            sessionId,
            new AgentAttachmentUploadRequest(
                "note.txt",
                "text/plain",
                Encoding.UTF8.GetBytes("attachment body")
            )
        );
        var userTurn = runtime.SessionService.AppendUserTurn(
            sessionId,
            AgentMessageRole.User,
            "original message",
            [storedAttachment]
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            attachmentService: runtime.AttachmentService
        );
        await viewModel.InitializeAsync();
        var row = viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>()
            .Single(message => message.RowId == userTurn.TurnId);
        Assert.False(viewModel.IsComposerExpanded);

        await viewModel.StartRollbackFromMessageCommand.ExecuteAsync(row);

        Assert.True(viewModel.IsRollbackPending);
        Assert.False(viewModel.IsComposerExpanded);
        Assert.Equal(userTurn.TurnId, viewModel.PendingRollbackTurnId);
        Assert.Equal("original message", viewModel.DraftMessage);
        Assert.Equal("Cancel Rollback", viewModel.ClearComposerButtonText);
        var pendingAttachment = Assert.Single(viewModel.PendingAttachments);
        Assert.Equal("note.txt", pendingAttachment.FileName);
        Assert.Equal("attachment body", Encoding.UTF8.GetString(pendingAttachment.UploadRequest.Content));

        viewModel.CancelRollbackCommand.Execute(null);

        Assert.False(viewModel.IsRollbackPending);
        Assert.Null(viewModel.PendingRollbackTurnId);
        Assert.Empty(viewModel.DraftMessage);
        Assert.Empty(viewModel.PendingAttachments);
        Assert.Equal("Clear", viewModel.ClearComposerButtonText);
    }

    [Fact]
    public async Task AgentChatViewModel_SendWhileRollbackPending_ReplacesSelectedMessage()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("new response"))
        );
        var sessionId = await runtime.CreateSessionAsync("noop");
        var retainedTurn = runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.User,
            "keep this"
        );
        var rollbackTurn = runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.User,
            "replace this"
        );
        var removedTurn = runtime.SessionService.AppendTextTurn(
            sessionId,
            AgentMessageRole.Assistant,
            "old response"
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            InlineAgentPresentationDispatcher.Instance,
            attachmentService: runtime.AttachmentService
        );
        await viewModel.InitializeAsync();
        var row = viewModel.Messages.OfType<AgentTextTranscriptRowViewModel>()
            .Single(message => message.RowId == rollbackTurn.TurnId);
        await viewModel.StartRollbackFromMessageCommand.ExecuteAsync(row);
        viewModel.DraftMessage = "replacement message";
        var rollbackCleared = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(AgentChatViewModel.IsRollbackPending)
                && !viewModel.IsRollbackPending)
            {
                rollbackCleared.TrySetResult();
            }
        };

        Assert.True(viewModel.SendMessageCommand.CanExecute(null));
        await viewModel.SendMessageCommand.ExecuteAsync(null);
        await rollbackCleared.Task.WaitAsync(TimeSpan.FromSeconds(10));

        var turns = runtime.SessionService.ListTurns(sessionId);
        Assert.Contains(turns, turn => turn.TurnId == retainedTurn.TurnId);
        Assert.DoesNotContain(turns, turn => turn.TurnId == rollbackTurn.TurnId);
        Assert.DoesNotContain(turns, turn => turn.TurnId == removedTurn.TurnId);
        Assert.Contains(turns, turn => turn.Role == AgentMessageRole.User && RenderTurnText(turn) == "replacement message");
        Assert.Contains(turns, turn => turn.Role == AgentMessageRole.Assistant && RenderTurnText(turn) == "new response");
        Assert.False(viewModel.IsRollbackPending);
    }

    [Fact]
    public async Task AgentChatViewModel_CommittedSubmissionWaitsForComposerReconciliation()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("unused"))
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
        viewModel.DraftMessage = "committed message";
        var composer = Assert.IsType<AgentComposerState>(
            typeof(AgentChatViewModel)
                .GetField("_composer", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(viewModel));
        var submission = Assert.IsType<AgentComposerSubmission>(
            composer.TryBeginSubmission(sessionId));
        submission.Commit();
        var completeSubmission = typeof(AgentChatViewModel).GetMethod(
            "CompleteOrRestoreComposerSubmissionAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        var reconciliation = Assert.IsAssignableFrom<Task<bool>>(completeSubmission.Invoke(
            viewModel,
            [viewModel.SelectedSession!, submission, true]));

        Assert.False(reconciliation.IsCompleted);
        submission.CompleteAuthoritativeReconciliation();
        Assert.True(await reconciliation);
    }

    [Fact]
    public async Task AgentChatViewModel_PreselectsFirstWorkspaceAndSession_WhenAvailable()
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

        Assert.Equal(runtime.CurrentWorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(runtime.CurrentProfileId, viewModel.SelectedProfile?.ProfileId);
        Assert.Equal(sessionId, viewModel.SelectedSession?.SessionId);
        Assert.True(viewModel.CanUseChat);
        Assert.False(viewModel.ShowSetupInstructions);
        Assert.True(viewModel.ShowCollapsedComposer);
    }

    [Fact]
    public async Task AgentChatViewModel_ReloadsSessions_WhenWorkspaceChanges()
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
        var secondWorkspace = runtime.WorkspaceService.CreateWorkspace("Second Workspace");
        var secondSession = runtime.SessionService.CreateSession("Second Workspace Session", workspaceId: secondWorkspace.WorkspaceId);

        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace =>
            workspace.WorkspaceId == secondWorkspace.WorkspaceId
        );

        Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
        Assert.Equal(secondSession.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == sessionId);
        Assert.Contains(viewModel.Sessions, session => session.SessionId == secondSession.SessionId);
        Assert.True(viewModel.CanUseChat);

        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(workspace =>
            workspace.WorkspaceId == runtime.CurrentWorkspaceId
        );

        Assert.Equal(sessionId, viewModel.SelectedSession?.SessionId);
        Assert.Contains(viewModel.Sessions, session => session.SessionId == sessionId);
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == secondSession.SessionId);
    }

    [Fact]
    public async Task AgentChatViewModel_IgnoresStaleSelectedSessionFromAnotherWorkspace()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var firstSessionId = await runtime.CreateSessionAsync(toolId);
        var secondWorkspace = runtime.WorkspaceService.CreateWorkspace("Second Workspace");
        var secondSession = runtime.SessionService.CreateSession(
            "Second Workspace Session",
            workspaceId: secondWorkspace.WorkspaceId
        );
        var stateRoot = Path.Combine(
            Path.GetTempPath(),
            "sunder-agent-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(stateRoot);

        try
        {
            var selectionContext = new TestPackageContext(stateRoot);
            var selectionState = new AgentChatSelectionStateService(selectionContext);
            await selectionState.SaveSelectedWorkspaceIdAsync(secondWorkspace.WorkspaceId);
            await selectionContext.Storage.State.SetValueAsync("agent.chat.selectedSessionId", firstSessionId.ToString("N"));

            using var viewModel = RegressionTestAppViews.Chat(
                runtime.ProfileService,
                runtime.WorkspaceService,
                runtime.SessionService,
                runtime.PermissionService,
                runtime.RunCoordinator,
                selectionState
            );
            await viewModel.InitializeAsync();

            Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
            Assert.Equal(secondSession.SessionId, viewModel.SelectedSession?.SessionId);
            Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == firstSessionId);
            Assert.Contains(viewModel.Sessions, session => session.SessionId == secondSession.SessionId);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AgentChatViewModel_IgnoresWorkspaceScopedSelectedSessionFromAnotherWorkspace()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var firstSessionId = await runtime.CreateSessionAsync(toolId);
        var secondWorkspace = runtime.WorkspaceService.CreateWorkspace("Second Workspace");
        var secondSession = runtime.SessionService.CreateSession(
            "Second Workspace Session",
            workspaceId: secondWorkspace.WorkspaceId
        );
        var stateRoot = Path.Combine(
            Path.GetTempPath(),
            "sunder-agent-tests",
            Guid.NewGuid().ToString("N")
        );
        Directory.CreateDirectory(stateRoot);

        try
        {
            var selectionState = new AgentChatSelectionStateService(new TestPackageContext(stateRoot));
            await selectionState.SaveSelectedWorkspaceIdAsync(secondWorkspace.WorkspaceId);
            await selectionState.SaveSelectedSessionIdAsync(secondWorkspace.WorkspaceId, firstSessionId);

            using var viewModel = RegressionTestAppViews.Chat(
                runtime.ProfileService,
                runtime.WorkspaceService,
                runtime.SessionService,
                runtime.PermissionService,
                runtime.RunCoordinator,
                selectionState
            );
            await viewModel.InitializeAsync();

            Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
            Assert.Equal(secondSession.SessionId, viewModel.SelectedSession?.SessionId);
            Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == firstSessionId);
            Assert.Contains(viewModel.Sessions, session => session.SessionId == secondSession.SessionId);
        }
        finally
        {
            if (Directory.Exists(stateRoot))
            {
                Directory.Delete(stateRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task AgentChatViewModel_SelectsFallbackSession_WhenSelectedSessionIsDeleted()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var deletedSessionId = await runtime.CreateSessionAsync(toolId);
        var fallbackSession = runtime.SessionService.CreateSession(
            "Fallback Session",
            workspaceId: runtime.CurrentWorkspaceId
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sessions.Single(session =>
            session.SessionId == deletedSessionId
        );

        await runtime.SessionService.DeleteSessionAsync(deletedSessionId);

        Assert.Equal(fallbackSession.SessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal(fallbackSession.SessionId, viewModel.DisplayedSession?.SessionId);
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == deletedSessionId);
    }

    [Fact]
    public async Task AgentChatViewModel_RenamesSessionInline()
    {
        const string toolId = "noop";

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

        viewModel.BeginRenameSessionCommand.Execute(viewModel.SelectedSession);
        viewModel.SelectedSession!.RenameTitle = "Renamed Session";
        viewModel.SaveSessionRenameCommand.Execute(viewModel.SelectedSession);

        Assert.False(viewModel.SelectedSession.IsRenameActive);
        Assert.Empty(viewModel.SelectedSession.RenameTitle);
        Assert.Equal("Renamed Session", viewModel.SelectedSession?.Title);
        Assert.Equal("Renamed Session", runtime.SessionService.GetSession(sessionId)?.Title);
    }

    [Fact]
    public async Task AgentChatViewModel_DeleteSessionCommand_RemovesSessionAndCancelsInlineRename()
    {
        const string toolId = "noop";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var deletedSessionId = await runtime.CreateSessionAsync(toolId);
        var fallbackSession = runtime.SessionService.CreateSession(
            "Fallback Session",
            workspaceId: runtime.CurrentWorkspaceId
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        var deletedSession = viewModel.Sessions.Single(session =>
            session.SessionId == deletedSessionId
        );
        viewModel.SelectedSession = deletedSession;
        viewModel.BeginRenameSessionCommand.Execute(deletedSession);

        viewModel.DeleteSessionCommand.Execute(deletedSession);

        Assert.False(deletedSession.IsRenameActive);
        Assert.Empty(deletedSession.RenameTitle);
        Assert.Null(runtime.SessionService.GetSession(deletedSessionId));
        Assert.DoesNotContain(viewModel.Sessions, session => session.SessionId == deletedSessionId);
        Assert.Equal(fallbackSession.SessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task AgentChatViewModel_CreateSession_UsesNumberedSessionTitles()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Package Developer");
        var workspace = runtime.WorkspaceService.CreateWorkspace("Test Workspace");
        runtime.SessionService.CreateSession("Package Developer Session 2", workspaceId: workspace.WorkspaceId);
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        viewModel.SelectedWorkspace = viewModel.Workspaces.Single(item => item.WorkspaceId == workspace.WorkspaceId);
        viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId);

        viewModel.CreateSessionCommand.Execute(null);

        Assert.Equal("Session 3", viewModel.SelectedSession?.Title);
        Assert.Equal("Session 3", runtime.SessionService.GetSession(viewModel.SelectedSession!.SessionId)?.Title);
        Assert.Equal(workspace.WorkspaceId, runtime.SessionService.GetSession(viewModel.SelectedSession.SessionId)?.WorkspaceId);
    }

    [Fact]
    public async Task AgentChatViewModel_CreateSession_PersistsSelectedWorkspaceAcrossStoreReopen()
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done"))
        );
        var profile = await runtime.ProfileService.CreateProfileAsync("Package Developer");
        var otherWorkspace = runtime.WorkspaceService.CreateWorkspace("Other Workspace");
        var selectedWorkspace = runtime.WorkspaceService.CreateWorkspace("Selected Workspace");
        var selectionState = new AgentChatSelectionStateService(new TestPackageContext(runtime.RootPath));
        Guid sessionId;

        using (var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            selectionState
        ))
        {
            await viewModel.InitializeAsync();
            viewModel.SelectedWorkspace = viewModel.Workspaces.Single(item => item.WorkspaceId == selectedWorkspace.WorkspaceId);
            viewModel.SelectedProfile = viewModel.Profiles.Single(item => item.ProfileId == profile.ProfileId);

            viewModel.CreateSessionCommand.Execute(null);

            sessionId = viewModel.SelectedSession!.SessionId;
            Assert.Equal(selectedWorkspace.WorkspaceId, viewModel.SelectedSession.Session.WorkspaceId);
        }

        var reopenedStore = new AgentLocalStore(new TestPackageContext(runtime.RootPath));
        var reopenedSessionService = new AgentSessionService(reopenedStore);

        var reopenedSession = reopenedSessionService.GetSession(sessionId);
        Assert.NotNull(reopenedSession);
        Assert.Equal(selectedWorkspace.WorkspaceId, reopenedSession!.WorkspaceId);
        Assert.Equal(selectedWorkspace.WorkspaceId, await selectionState.GetSelectedWorkspaceIdAsync());
        Assert.Equal(sessionId, await selectionState.GetSelectedSessionIdAsync(selectedWorkspace.WorkspaceId));
        Assert.NotEqual(sessionId, await selectionState.GetSelectedSessionIdAsync(AgentWorkspaceService.UnassignedSessionsWorkspaceId));
        Assert.Contains(
            reopenedSessionService.ListSessionsForWorkspace(selectedWorkspace.WorkspaceId),
            session => session.SessionId == sessionId
        );
        Assert.DoesNotContain(
            reopenedSessionService.ListSessionsForWorkspace(otherWorkspace.WorkspaceId),
            session => session.SessionId == sessionId
        );
        Assert.DoesNotContain(
            reopenedSessionService.ListSessionsForWorkspace(AgentWorkspaceService.UnassignedSessionsWorkspaceId),
            session => session.SessionId == sessionId
        );
    }

    [Fact]
    public async Task AgentChatViewModel_HidesChildSessionsFromSessionSelector()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        Assert.Contains(viewModel.Sessions, session => session.SessionId == parentSessionId);
        Assert.DoesNotContain(
            viewModel.Sessions,
            session => session.SessionId == childSession.SessionId
        );
        Assert.Equal(parentSessionId, viewModel.SelectedSession?.SessionId);
    }

    [Fact]
    public async Task AgentChatViewModel_OpenChildSession_OpensSubsessionsViewWithoutChangingSelector()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "Child transcript content."
        );
        var shellViewService = new CapturingShellViewService();

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator,
            shellViewService: shellViewService
        );
        await viewModel.InitializeAsync();

        await viewModel.OpenChildSessionCommand.ExecuteAsync(
            new AgentChildSessionLinkViewModel(
                childSession.SessionId,
                childSession.Title,
                "Exploration subagent"
            )
        );

        Assert.Equal(parentSessionId, viewModel.SelectedSession?.SessionId);
        Assert.Equal(parentSessionId, viewModel.DisplayedSession?.SessionId);
        Assert.True(viewModel.ShowCollapsedComposer);
        Assert.DoesNotContain(
            viewModel.Sessions,
            session => session.SessionId == childSession.SessionId
        );
        Assert.Equal(SubagentConstants.SubsessionsViewId, shellViewService.OpenedViewId);
        Assert.NotNull(shellViewService.Parameters);
        Assert.Equal(
            childSession.SessionId.ToString("D"),
            shellViewService.Parameters![SubagentConstants.SubsessionNavigationSessionIdKey]
        );
    }

    [Fact]
    public async Task AgentChatViewModel_RestoresChildSelectionAsRootSession()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        var selectionRootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-agent-selection-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var selectionContext = new TestPackageContext(selectionRootPath);
            var selectionState = new AgentChatSelectionStateService(selectionContext);
            await selectionState.SaveSelectedWorkspaceIdAsync(runtime.CurrentWorkspaceId);
            await selectionState.SaveSelectedSessionIdAsync(runtime.CurrentWorkspaceId, childSession.SessionId);
            await selectionState.SaveSelectedProfileIdAsync(runtime.CurrentProfileId);

            using var viewModel = RegressionTestAppViews.Chat(
                runtime.ProfileService,
                runtime.WorkspaceService,
                runtime.SessionService,
                runtime.PermissionService,
                runtime.RunCoordinator,
                selectionState
            );
            await viewModel.InitializeAsync();

            Assert.Equal(parentSessionId, viewModel.SelectedSession?.SessionId);
            Assert.Contains(viewModel.Sessions, session => session.SessionId == parentSessionId);
            Assert.DoesNotContain(
                viewModel.Sessions,
                session => session.SessionId == childSession.SessionId
            );
        }
        finally
        {
            try
            {
                if (Directory.Exists(selectionRootPath))
                {
                    Directory.Delete(selectionRootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }

    [Fact]
    public void AgentToolInvocationRow_ExposesChildSessionLinkFromTaskResult()
    {
        var now = DateTimeOffset.UtcNow;
        var childSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var argumentsJson = JsonSerializer.Serialize(
            new { description = "Explore current repository state", subagent_type = "Exploration" }
        );
        var structuredPayloadJson = JsonSerializer.Serialize(
            new
            {
                childSessionId,
                childSessionTitle = "Explore current repository state",
                subagentName = "Exploration",
            }
        );
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [],
            now,
            now
        );
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            "done",
            "task-call",
            "task",
            argumentsJson,
            "Subagent completed.",
            structuredPayloadJson,
            null,
            false,
            false,
            null,
            childSessionId.ToString("N")
        );

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            turn,
            item,
            new AgentToolPresentationService(),
            childSessionLinksResolver: (_, _) =>
            [
                new AgentChildSessionLinkViewModel(
                    childSessionId,
                    "Explore current repository state",
                    "Exploration subagent"),
            ]
        );

        Assert.True(row.HasChildSessionLink);
        Assert.NotNull(row.ChildSessionLink);
        Assert.Equal(childSessionId, row.ChildSessionLink!.SessionId);
        Assert.Equal("Explore current repository state", row.ChildSessionLink.Title);
        Assert.Equal("Exploration subagent", row.ChildSessionLink.Subtitle);
        Assert.Equal(
            "Exploration subagent · Explore current repository state",
            row.ChildSessionLink.DisplayText
        );
    }

    [Fact]
    public void AgentToolInvocationRow_ExposesChildSessionLinksFromDelegateTasksResult()
    {
        var now = DateTimeOffset.UtcNow;
        var firstChildSessionId = Guid.NewGuid();
        var secondChildSessionId = Guid.NewGuid();
        var turnId = Guid.NewGuid();
        var argumentsJson = JsonSerializer.Serialize(
            new
            {
                tasks = new[]
                {
                    new { description = "Explore repository", subagent_type = "Exploration" },
                    new { description = "Review test risks", subagent_type = "Reviewer" },
                },
            }
        );
        var structuredPayloadJson = JsonSerializer.Serialize(
            new
            {
                tasks = new[]
                {
                    new
                    {
                        childSessionId = firstChildSessionId,
                        childSessionTitle = "Explore repository",
                        subagentName = "Exploration",
                    },
                    new
                    {
                        childSessionId = secondChildSessionId,
                        childSessionTitle = "Review test risks",
                        subagentName = "Reviewer",
                    },
                },
            }
        );
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [],
            now,
            now
        );
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            "done",
            "delegate-call",
            SubagentConstants.DelegateTasksToolId,
            argumentsJson,
            "Delegated subagent tasks completed.",
            structuredPayloadJson,
            null,
            false,
            false,
            null,
            null
        );

        using var row = TranscriptToolTestHarness.CreateAgentRow(
            turn,
            item,
            new AgentToolPresentationService(),
            childSessionLinksResolver: (_, _) =>
            [
                new AgentChildSessionLinkViewModel(
                    firstChildSessionId,
                    "Explore repository",
                    "Exploration subagent"),
                new AgentChildSessionLinkViewModel(
                    secondChildSessionId,
                    "Review test risks",
                    "Reviewer subagent"),
            ]
        );

        Assert.True(row.HasChildSessionLinks);
        Assert.Equal(2, row.ChildSessionLinks.Count);
        Assert.Equal(firstChildSessionId, row.ChildSessionLinks[0].SessionId);
        Assert.Equal(
            "Exploration subagent · Explore repository",
            row.ChildSessionLinks[0].DisplayText
        );
        Assert.Equal(secondChildSessionId, row.ChildSessionLinks[1].SessionId);
        Assert.Equal("Reviewer subagent · Review test risks", row.ChildSessionLinks[1].DisplayText);
    }

    [Fact]
    public async Task AgentChatViewModel_TaskRowLinksChildSessionBeforeTaskResult()
    {
        const string toolId = "fetch_page";
        const string toolCallId = "task-call";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var argumentsJson = JsonSerializer.Serialize(
            new
            {
                description = "Explore current repository state",
                prompt = "Inspect the repository.",
                subagent_type = "Exploration",
            }
        );
        runtime.SessionService.AppendToolCallTurn(
            parentSessionId,
            AgentMessageRole.Assistant,
            toolCallId,
            "task",
            argumentsJson
        );
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        var row = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        Assert.True(row.HasChildSessionLink);
        Assert.NotNull(row.ChildSessionLink);
        Assert.Equal(childSession.SessionId, row.ChildSessionLink!.SessionId);
        Assert.Equal("Explore current repository state", row.ChildSessionLink.Title);
    }

    [Fact]
    public async Task AgentChatViewModel_ChildSessionLinksShowAndRefreshStatus()
    {
        const string toolId = "fetch_page";
        const string toolCallId = "task-call";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        runtime.SessionService.AppendToolCallTurn(
            parentSessionId,
            AgentMessageRole.Assistant,
            toolCallId,
            "task",
            "{\"description\":\"Explore\",\"prompt\":\"Inspect.\",\"subagent_type\":\"Explore\"}"
        );
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        var runRevision = runtime.SessionService.GetNextRunRevision(childSession.SessionId);
        runtime.SessionService.SaveCheckpoint(
            childSession.SessionId,
            runRevision,
            AgentRunStatus.Running,
            "Child running."
        );
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();
        var row = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        var link = Assert.Single(row.ChildSessionLinks);

        Assert.Equal("Running", link.StatusText);
        Assert.Equal("i", link.StatusIconText);

        runtime.SessionService.SaveCheckpoint(
            childSession.SessionId,
            runRevision,
            AgentRunStatus.Completed,
            "Child done."
        );

        var refreshedLink = Assert.Single(row.ChildSessionLinks);
        Assert.Same(link, refreshedLink);
        Assert.Equal("Done", refreshedLink.StatusText);
        Assert.Equal("✓", refreshedLink.StatusIconText);
    }

    [Fact]
    public async Task AgentChatViewModel_DelegateTasksRowLinksChildSessionsBeforeTaskResult()
    {
        const string toolId = "fetch_page";
        const string toolCallId = "delegate-call";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var argumentsJson = JsonSerializer.Serialize(
            new
            {
                tasks = new[]
                {
                    new
                    {
                        description = "Explore current repository state",
                        prompt = "Inspect the repository.",
                        subagent_type = "Exploration",
                    },
                    new
                    {
                        description = "Review test coverage",
                        prompt = "Inspect tests.",
                        subagent_type = "Reviewer",
                    },
                },
            }
        );
        runtime.SessionService.AppendToolCallTurn(
            parentSessionId,
            AgentMessageRole.Assistant,
            toolCallId,
            SubagentConstants.DelegateTasksToolId,
            argumentsJson
        );
        var firstChild = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        var secondChild = runtime.SessionService.CreateSession(
            "Review test coverage",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            parentRunId: Guid.NewGuid(),
            parentRunRevision: 1,
            parentToolCallId: toolCallId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );

        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService,
            runtime.WorkspaceService,
            runtime.SessionService,
            runtime.PermissionService,
            runtime.RunCoordinator
        );
        await viewModel.InitializeAsync();

        var row = Assert.Single(viewModel.Messages.OfType<AgentToolInvocationRowViewModel>());
        Assert.True(row.HasChildSessionLinks);
        Assert.Equal(2, row.ChildSessionLinks.Count);
        Assert.Equal(firstChild.SessionId, row.ChildSessionLinks[0].SessionId);
        Assert.Equal(secondChild.SessionId, row.ChildSessionLinks[1].SessionId);
    }

    [Fact]
    public async Task SubsessionsViewModel_NavigationSelectsChildSessionAndLoadsTranscript()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "Child transcript content."
        );
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var viewModel = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter, InlineAgentPresentationDispatcher.Instance);

        await viewModel.OnNavigatedToAsync(
            new PackageViewNavigationContext(
                SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?>
                {
                    [SubagentConstants.SubsessionNavigationSessionIdKey] =
                        childSession.SessionId.ToString("D"),
                }
            )
        );

        Assert.Equal(childSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.DoesNotContain(
            viewModel.Subsessions,
            session => session.SessionId == parentSessionId
        );
        Assert.Contains(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(),
            row => row.Content == "Child transcript content."
        );

        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "Live child update."
        );

        await WaitUntilAsync(() => viewModel.Messages.Count >= 2);
        Assert.Contains(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(),
            row => row.Content == "Live child update."
        );
    }

    [Fact]
    public async Task SubsessionsViewModel_RepeatedNavigationToSelectedSubsessionIsNoOp()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var firstChildSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        var secondChildSession = runtime.SessionService.CreateSession(
            "Review implementation details",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.AppendTextTurn(
            firstChildSession.SessionId,
            AgentMessageRole.Assistant,
            "First transcript content."
        );
        runtime.SessionService.AppendTextTurn(
            secondChildSession.SessionId,
            AgentMessageRole.Assistant,
            "Second transcript content."
        );
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var viewModel = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter, InlineAgentPresentationDispatcher.Instance);
        var firstNavigation = new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] =
                    firstChildSession.SessionId.ToString("D"),
            }
        );
        var secondNavigation = new PackageViewNavigationContext(
            SubagentConstants.SubsessionsViewId,
            new Dictionary<string, string?>
            {
                [SubagentConstants.SubsessionNavigationSessionIdKey] =
                    secondChildSession.SessionId.ToString("D"),
            }
        );

        await viewModel.OnNavigatedToAsync(firstNavigation);
        var selectedSubsession = viewModel.SelectedSubsession;
        var transcriptRow = Assert.Single(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()
        );

        await viewModel.OnNavigatedToAsync(firstNavigation);

        Assert.Same(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Same(
            transcriptRow,
            Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>())
        );

        await viewModel.OnNavigatedToAsync(secondNavigation);

        Assert.Equal(secondChildSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        Assert.NotSame(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Contains(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(),
            row => row.Content == "Second transcript content."
        );
    }

    [Fact]
    public async Task SubsessionsViewModel_SessionChangedUpdatesStatusWithoutReloadingTranscript()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.User,
            "Child task request."
        );
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var viewModel = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter, InlineAgentPresentationDispatcher.Instance);
        await viewModel.OnNavigatedToAsync(
            new PackageViewNavigationContext(
                SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?>
                {
                    [SubagentConstants.SubsessionNavigationSessionIdKey] =
                        childSession.SessionId.ToString("D"),
                }
            )
        );
        var selectedSubsession = viewModel.SelectedSubsession;
        var transcriptRow = Assert.Single(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()
        );
        var runRevision = runtime.SessionService.GetNextRunRevision(childSession.SessionId);
        var runningApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveRunningState()
        {
            if (viewModel.SelectedSubsession?.IsRunActive == true
                && viewModel.RunActivityRow.IsVisible
                && viewModel.RunActivityRow.ThinkingText.StartsWith("Running Task", StringComparison.Ordinal))
            {
                runningApplied.TrySetResult();
            }
        }
        viewModel.TranscriptChanged += ObserveRunningState;

        runtime.SessionService.SaveCheckpoint(
            childSession.SessionId,
            runRevision,
            AgentRunStatus.Running,
            "Executing tool 'task'."
        );
        ObserveRunningState();
        await runningApplied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.TranscriptChanged -= ObserveRunningState;

        Assert.Same(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Same(
            transcriptRow,
            Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>())
        );
        var activityRow = viewModel.RunActivityRow;
        Assert.DoesNotContain(activityRow, viewModel.Messages);
        Assert.Same(activityRow, viewModel.TranscriptItems[^2]);
        Assert.True(activityRow.IsVisible);
        Assert.StartsWith("Running Task", activityRow.ThinkingText, StringComparison.Ordinal);
        Assert.True(viewModel.SelectedSubsession?.IsRunActive);
        var completedApplied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveCompletedState()
        {
            if (viewModel.SelectedSubsession?.IsRunActive == false
                && !viewModel.RunActivityRow.IsVisible)
            {
                completedApplied.TrySetResult();
            }
        }
        viewModel.TranscriptChanged += ObserveCompletedState;

        runtime.SessionService.SaveCheckpoint(
            childSession.SessionId,
            runRevision,
            AgentRunStatus.Completed,
            "Done."
        );
        ObserveCompletedState();
        await completedApplied.Task.WaitAsync(TimeSpan.FromSeconds(3));
        viewModel.TranscriptChanged -= ObserveCompletedState;

        Assert.Same(selectedSubsession, viewModel.SelectedSubsession);
        Assert.Same(
            transcriptRow,
            Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>())
        );
        Assert.Same(activityRow, viewModel.RunActivityRow);
        Assert.Same(activityRow, viewModel.TranscriptItems[^2]);
        Assert.False(activityRow.IsVisible);
        Assert.False(viewModel.SelectedSubsession?.IsRunActive);
    }

    [Fact]
    public async Task SubsessionListItemViewModel_ApplyCheckpoint_DoesNotTouchAvaloniaResourcesOffUiThread()
    {
        var now = DateTimeOffset.UtcNow;
        var sessionId = Guid.NewGuid();
        var session = new SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionRecord
        {
            SessionId = sessionId,
            Title = "Background Child",
            State = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentSessionState.Active,
            CreatedAtUtc = new SunderRpcOffsetDateTime(now.ToString("O")),
            UpdatedAtUtc = new SunderRpcOffsetDateTime(now.ToString("O")),
            AgentKind = "subagent",
        };
        var checkpoint = new SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunCheckpointRecord
        {
            CheckpointId = Guid.NewGuid(),
            SessionId = sessionId,
            RunRevision = 1,
            Status = SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentRunStatus.Running,
            Summary = "Running.",
            CreatedAtUtc = new SunderRpcOffsetDateTime(now.ToString("O")),
        };
        SubsessionListItemViewModel? item = null;

        var exception = await Record.ExceptionAsync(async () =>
        {
            item = await Task.Run(
                () => new SubsessionListItemViewModel(session, "Subagent", checkpoint)
            );
        });

        Assert.Null(exception);
        Assert.NotNull(item);
        Assert.Equal("Running", item!.StatusBadgeText);
    }

    [Fact]
    public async Task SubsessionsViewModel_TurnChangedAppendsWithoutReloadingExistingRows()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "First transcript content."
        );
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var viewModel = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter, InlineAgentPresentationDispatcher.Instance);
        await viewModel.OnNavigatedToAsync(
            new PackageViewNavigationContext(
                SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?>
                {
                    [SubagentConstants.SubsessionNavigationSessionIdKey] =
                        childSession.SessionId.ToString("D"),
                }
            )
        );
        var firstRow = Assert.Single(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>()
        );

        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "Second transcript content."
        );

        await WaitUntilAsync(() => viewModel.Messages
            .OfType<SubsessionTextTranscriptRowViewModel>()
            .Count() == 2);
        var textRows = viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>().ToArray();
        Assert.Equal(2, textRows.Length);
        Assert.Same(firstRow, textRows[0]);
        Assert.Equal("Second transcript content.", textRows[1].Content);
    }

    [Fact]
    public async Task SubsessionsViewModel_DetachedTranscriptBuffersLiveRowsUntilNewerRowsLoad()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var childSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "Initial child transcript."
        );
        var rpc = Assert.IsType<RegressionTestRpcClient>(runtime.ExtensionCatalog.RpcForCaller("younics.ai.agent.subagents"));
        var coreProvider = Assert.Single((await rpc.DiscoverAsync("younics.ai.agent.runtime.catalog")).Providers);
        var upstreamReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveCoreWatch(string caller, SunderRpcProviderSnapshot provider, string service, string method)
        {
            if (caller == "younics.ai.agent.subagents" && service == "runtime-catalog" && method == "watch"
                && provider.ActivationId == coreProvider.ActivationId && provider.ActivationEpoch == coreProvider.ActivationEpoch
                && provider.SessionGeneration == coreProvider.SessionGeneration && provider.Endpoint == coreProvider.Endpoint)
                upstreamReady.TrySetResult();
        }
        rpc.State.ProviderStreamItemObserved += ObserveCoreWatch;
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var viewModel = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter, InlineAgentPresentationDispatcher.Instance);
        var observedEvents = new System.Collections.Concurrent.ConcurrentQueue<string>();
        runtimeAdapter.ResnapshotRequired += () => observedEvents.Enqueue("resnapshot");
        runtimeAdapter.TurnChanged += (sessionId, turn) => observedEvents.Enqueue($"turn:{sessionId:N}:{turn.TurnId:N}");
        try
        {
            await viewModel.OnNavigatedToAsync(new PackageViewNavigationContext(SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?> { [SubagentConstants.SubsessionNavigationSessionIdKey] = childSession.SessionId.ToString("D") }));
            await upstreamReady.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { rpc.State.ProviderStreamItemObserved -= ObserveCoreWatch; }

        // Initialization starts observation, but a navigation return is not proof
        // that bootstrap resets have reached the presentation. An unrelated parent
        // turn/session pair provides an ordered fence on the genuine generated stream.
        var marker = "ordinary-live-fence-" + Guid.NewGuid().ToString("N");
        var markerSeen = 0;
        var deliveredTurn = new TaskCompletionSource<Guid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deliveredSession = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<Guid, SubagentsApp::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentTurnRecord> onTurn = (id, turn) =>
        {
            if (id == parentSessionId && turn.Items.Any(item => item.TextContent == marker))
            {
                Interlocked.Exchange(ref markerSeen, 1);
                deliveredTurn.TrySetResult(turn.TurnId);
            }
        };
        Action<Guid> onSession = id =>
        {
            if (id == parentSessionId && Volatile.Read(ref markerSeen) != 0) deliveredSession.TrySetResult();
        };
        runtimeAdapter.TurnChanged += onTurn;
        runtimeAdapter.SessionChanged += onSession;
        try
        {
            var markerTurn = runtime.SessionService.AppendTextTurn(parentSessionId, AgentMessageRole.Assistant, marker);
            Assert.Equal(markerTurn.TurnId, await deliveredTurn.Task.WaitAsync(TimeSpan.FromSeconds(2)));
            await deliveredSession.Task.WaitAsync(TimeSpan.FromSeconds(2));
        }
        finally { runtimeAdapter.TurnChanged -= onTurn; runtimeAdapter.SessionChanged -= onSession; }
        var refresh = typeof(SubsessionsViewModel).GetField("_runtimeRefresh", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel)!;
        await ((Task)refresh.GetType().GetProperty("WhenIdle")!.GetValue(refresh)!).WaitAsync(TimeSpan.FromSeconds(2));
        // Capture after refresh: that refresh can start an authoritative transcript read.
        await ((Task)typeof(SubsessionsViewModel).GetField("_transcriptLoadOperation", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(viewModel)!).WaitAsync(TimeSpan.FromSeconds(2));
        SubsessionTextTranscriptRowViewModel? initialRow = null;
        await InlineAgentPresentationDispatcher.Instance.InvokeAsync(() =>
        {
            Assert.False(viewModel.IsTranscriptLoading);
            initialRow = Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>());
            Assert.True(viewModel.DetachTranscriptFromLatest());
            observedEvents.Enqueue("detached-confirmed-after-bootstrap-fence");
        });
        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "Live child update while detached."
        );

        try { await WaitUntilAsync(() => viewModel.HasNewerTranscriptRows); }
        catch (OperationCanceledException error)
        {
            var state = "";
            await InlineAgentPresentationDispatcher.Instance.InvokeAsync(() => state =
                $"following={viewModel.IsTranscriptFollowingLatest}; loading={viewModel.IsTranscriptLoading}; newer={viewModel.HasNewerTranscriptRows}; "
                + $"error={viewModel.HasLoadError}; rows={viewModel.Messages.Count}; events={string.Join(",", observedEvents)}");
            throw new InvalidOperationException("Detached transcript did not advertise buffered live rows: " + state, error);
        }
        Assert.Same(
            initialRow,
            Assert.Single(viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>())
        );
        Assert.True(viewModel.HasNewerTranscriptRows);
        Assert.DoesNotContain(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(),
            row => row.Content == "Live child update while detached."
        );

        var loaded = await viewModel.LoadNewerTranscriptRowsAsync();

        Assert.True(loaded);
        Assert.False(viewModel.HasNewerTranscriptRows);
        Assert.Contains(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(),
            row => row.Content == "Live child update while detached."
        );

        runtime.SessionService.AppendTextTurn(
            childSession.SessionId,
            AgentMessageRole.Assistant,
            "Live child update after resume."
        );

        await WaitUntilAsync(() => ContainsSubsessionTextRow(
            viewModel,
            "Live child update after resume."));
        Assert.Contains(
            viewModel.Messages.OfType<SubsessionTextTranscriptRowViewModel>(),
            row => row.Content == "Live child update after resume."
        );
    }

    [Fact]
    public async Task SubsessionsViewModel_PreservesExpandedToolRowsWhenSelectedSubsessionReorders()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var parentSessionId = await runtime.CreateSessionAsync(toolId);
        var parentSession = runtime.SessionService.GetSession(parentSessionId)!;
        var firstChildSession = runtime.SessionService.CreateSession(
            "Explore current repository state",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        runtime.SessionService.AppendToolCallTurn(
            firstChildSession.SessionId,
            AgentMessageRole.Assistant,
            "call-1",
            "read",
            "{\"path\":\"/workspace/README.md\"}"
        );
        runtime.SessionService.CreateSession(
            "Review implementation details",
            parentSessionId: parentSessionId,
            rootSessionId: parentSession.RootSessionId ?? parentSession.SessionId,
            profileId: runtime.CurrentProfileId,
            agentKind: "subagent"
        );
        using var runtimeAdapter = WorkerBackedSubagentManagementGateway.ForRuntime(
            new TestPackageContext(runtime.RootPath), runtime.ExtensionCatalog);
        using var viewModel = new SubsessionsViewModel(
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter,
            runtimeAdapter, InlineAgentPresentationDispatcher.Instance);
        await viewModel.OnNavigatedToAsync(
            new PackageViewNavigationContext(
                SubagentConstants.SubsessionsViewId,
                new Dictionary<string, string?>
                {
                    [SubagentConstants.SubsessionNavigationSessionIdKey] =
                        firstChildSession.SessionId.ToString("D"),
                }
            )
        );
        var toolRow = Assert.Single(
            viewModel.Messages.OfType<SubsessionToolInvocationRowViewModel>()
        );
        var expandedDetails = await TranscriptToolTestHarness.ExpandAsync(toolRow);
        var moveObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Subsessions.CollectionChanged += (_, args) =>
        {
            if (args.Action != NotifyCollectionChangedAction.Move)
            {
                return;
            }

            viewModel.SelectedSubsession = null;
            moveObserved.TrySetResult();
        };

        runtime.SessionService.SaveCheckpoint(
            firstChildSession.SessionId,
            runtime.SessionService.GetNextRunRevision(firstChildSession.SessionId),
            AgentRunStatus.Running,
            "Child running."
        );
        await moveObserved.Task.WaitAsync(TimeSpan.FromSeconds(3));

        Assert.Equal(firstChildSession.SessionId, viewModel.SelectedSubsession?.SessionId);
        var preservedToolRow = Assert.Single(
            viewModel.Messages.OfType<SubsessionToolInvocationRowViewModel>()
        );
        Assert.Same(toolRow, preservedToolRow);
        Assert.True(preservedToolRow.IsExpanded);
        Assert.Same(expandedDetails, preservedToolRow.ExpandedDetails);
    }

    [Fact]
    public async Task SubsessionToolInvocationRow_OutputOnlyDetailsRemainLazyAndExpandable()
    {
        var now = DateTimeOffset.UtcNow;
        var turnId = Guid.NewGuid();
        var turn = new AgentTurnRecord(
            turnId,
            Guid.NewGuid(),
            AgentMessageRole.Tool,
            AgentTurnKind.ToolResult,
            [],
            now,
            now);
        var item = new AgentTurnItemRecord(
            Guid.NewGuid(),
            turnId,
            0,
            AgentTurnItemKind.ToolResult,
            "subsession output",
            "call-1",
            "output_only",
            "{}",
            "Completed.",
            null,
            null,
            false,
            false,
            null,
            null);
        using var row = TranscriptToolTestHarness.CreateSubsessionRow(turn, item);

        Assert.True(row.HasDetails);
        Assert.False(row.IsExpanded);
        Assert.False(row.HasMaterializedDetails);

        var details = await TranscriptToolTestHarness.ExpandAsync(row);

        Assert.True(details.HasOutput);
        Assert.False(details.HasMarkdownDetails);
        Assert.True(row.IsExpanded);
        Assert.Same(details, row.ExpandedDetails);
    }

    [Fact]
    public async Task AgentChatViewModel_RestoresPersistedWorkspaceAndSession_WhenStillValid()
    {
        const string toolId = "fetch_page";

        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("done")),
            new TestTool(toolId)
        );
        var firstSessionId = await runtime.CreateSessionAsync(toolId);
        var secondProfile = await runtime.ProfileService.CreateProfileAsync("Second Profile");
        var secondWorkspace = runtime.WorkspaceService.CreateWorkspace("Second Workspace");
        var secondSession = runtime.SessionService.CreateSession(
            "Second Session",
            workspaceId: secondWorkspace.WorkspaceId
        );
        var selectionRootPath = Path.Combine(
            Path.GetTempPath(),
            "sunder-agent-selection-tests",
            Guid.NewGuid().ToString("N")
        );

        try
        {
            var selectionContext = new TestPackageContext(selectionRootPath);
            var selectionState = new AgentChatSelectionStateService(selectionContext);
            await selectionState.SaveSelectedWorkspaceIdAsync(secondWorkspace.WorkspaceId);
            await selectionState.SaveSelectedSessionIdAsync(secondWorkspace.WorkspaceId, secondSession.SessionId);
            await selectionState.SaveSelectedProfileIdAsync(secondProfile.ProfileId);

            using var viewModel = RegressionTestAppViews.Chat(
                runtime.ProfileService,
                runtime.WorkspaceService,
                runtime.SessionService,
                runtime.PermissionService,
                runtime.RunCoordinator,
                selectionState
            );
            await viewModel.InitializeAsync();

            Assert.Equal(secondWorkspace.WorkspaceId, viewModel.SelectedWorkspace?.WorkspaceId);
            Assert.Equal(secondSession.SessionId, viewModel.SelectedSession?.SessionId);
            Assert.Equal(secondProfile.ProfileId, viewModel.SelectedProfile?.ProfileId);
            Assert.NotEqual(firstSessionId, viewModel.SelectedSession?.SessionId);
        }
        finally
        {
            try
            {
                if (Directory.Exists(selectionRootPath))
                {
                    Directory.Delete(selectionRootPath, recursive: true);
                }
            }
            catch
            {
                // Test cleanup should not hide assertion failures.
            }
        }
    }
}
