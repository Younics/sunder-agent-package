extern alias AgentApp;
extern alias McpWorker;

using System.Text.Json;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Avalonia;
using LiveMarkdown.Avalonia;
using McpWorker::Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;
using static Sunder.Package.Agent.Presentation.Tests.RenderedChatFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedTranscriptGeometry;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class AgentChatPublicationTests
{
    [AvaloniaFact]
    public async Task AgentChatViewModel_QueuedAuthoritativeAddProjectsBeforeComposerClearsWithoutReplacement()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Send profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Send workspace");
        var session = services.SessionService.CreateSession(
            "Send session",
            workspaceId: workspace.WorkspaceId);
        var sessionGateway = new CountingSessionGateway(services.SessionService);
        using var viewModel = RegressionTestAppViews.Chat(
            profileService,
            services.WorkspaceService,
            sessionGateway,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance);
        await viewModel.InitializeAsync();
        viewModel.SelectedSession = viewModel.Sessions.Single(item => item.SessionId == session.SessionId);
        const string message = "queued authoritative message";
        viewModel.DraftMessage = message;
        var composer = typeof(AgentChatViewModel)
            .GetField(
                "_composer",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(viewModel)!;
        var beginSubmission = composer.GetType()
            .GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public)
            .Single(method => method.Name == "TryBeginSubmission"
                               && method.GetParameters().Length == 1);
        var submission = beginSubmission.Invoke(
            composer,
            [session.SessionId]);
        Assert.NotNull(submission);
        var draftRevisions = (System.Collections.IDictionary)typeof(AgentChatViewModel)
            .GetField("_draftRevisions", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(viewModel)!;
        submission.GetType().GetProperty("DraftRevision")!.SetValue(submission, draftRevisions[session.SessionId] ?? 0L);
        var recentReadsBeforeCompletion = sessionGateway.RecentTranscriptReadCount;
        var turn = services.SessionService.AppendTextTurn(
            session.SessionId,
            AgentMessageRole.User,
            message);
        submission.GetType()
            .GetField(
                "<UserTurnId>k__BackingField",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(submission, turn.TurnId);
        var rowWasPresentWhenComposerCleared = false;
        viewModel.PropertyChanged += (_, eventArgs) =>
        {
            if (eventArgs.PropertyName == nameof(AgentChatViewModel.DraftMessage)
                && string.IsNullOrEmpty(viewModel.DraftMessage))
            {
                rowWasPresentWhenComposerCleared = viewModel.Messages.Any(row => row.RowId == turn.TurnId);
            }
        };
        var completeSubmission = typeof(AgentChatViewModel).GetMethod(
            "CompleteOrRestoreComposerSubmissionAsync",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

        var completed = await Assert.IsAssignableFrom<Task<bool>>(completeSubmission.Invoke(
            viewModel,
            [viewModel.SelectedSession!, submission, true]));
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);

        Assert.True(completed);
        Assert.True(rowWasPresentWhenComposerCleared);
        Assert.Empty(viewModel.DraftMessage);
        Assert.Single(viewModel.Messages, row => row.RowId == turn.TurnId);
        Assert.Equal(recentReadsBeforeCompletion, sessionGateway.RecentTranscriptReadCount);
        Assert.False(viewModel.IsTranscriptLoading);
    }

    [AvaloniaFact]
    public async Task AgentChatView_AssistantArrivalReplacesActivityWithoutExposingOlderPosition()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Live row profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Live row workspace");
        var session = services.SessionService.CreateSession(
            "Live row session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 8; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Existing response {index}: {new string('x', 100)}");
        }
        var runRevision = services.SessionService.GetNextRunRevision(session.SessionId);
        services.SessionService.SaveCheckpoint(
            session.SessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking.");

        using var view = CreateAgentChatView(scope, services, profileService);
        var window = new Window { Width = 800, Height = 360, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                    "younics.ai.agent.chat",
                    new Dictionary<string, string?>()))
                .AsTask()
                .WaitAsync(TimeSpan.FromSeconds(3));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var repeater = Assert.IsType<ItemsRepeater>(view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var jumpButton = Assert.IsType<Button>(view.FindControl<Button>("JumpToLatestTranscriptButton"));
            var completedLayoutDistances = new List<double>();
            var opacityWasHidden = false;
            var jumpBecameVisible = false;
            var collectionRemovals = 0;
            var observeLayouts = false;
            transcript.PropertyChanged += (_, change) =>
            {
                if (change.Property == Visual.OpacityProperty && transcript.Opacity == 0)
                {
                    opacityWasHidden = true;
                }
            };
            transcript.LayoutUpdated += (_, _) =>
            {
                if (observeLayouts && transcript.Viewport.Height > 0)
                {
                    completedLayoutDistances.Add(
                        transcript.Extent.Height - transcript.Viewport.Height - transcript.Offset.Y);
                }
            };
            viewModel.Messages.CollectionChanged += (_, eventArgs) =>
            {
                if (eventArgs.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                {
                    collectionRemovals++;
                }
            };
            jumpButton.PropertyChanged += (_, change) =>
            {
                if (change.Property == Visual.IsVisibleProperty && jumpButton.IsVisible)
                {
                    jumpBecameVisible = true;
                }
            };
            const string markdown = "## New assistant response";
            Assert.True(viewModel.RunActivityRow.IsVisible);
            AssertTranscriptTailGeometry(view, viewModel);

            observeLayouts = true;
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                markdown);
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentTextTranscriptRowViewModel>()
                .Any(row => row.Content == markdown));
            await WaitForPendingCoordinatorOperationsAsync(view);
            await WaitUntilAsync(() => repeater.TryGetElement(viewModel.Messages.Count - 1) is Control row
                                       && row.GetVisualDescendants()
                                           .OfType<MarkdownRenderer>()
                                           .Any(renderer => renderer.Opacity == 1));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            observeLayouts = false;

            Assert.False(opacityWasHidden);
            Assert.False(jumpBecameVisible);
            Assert.True(viewModel.RunActivityRow.IsVisible);
            Assert.Equal(0, collectionRemovals);
            Assert.NotEmpty(completedLayoutDistances);
            Assert.All(
                completedLayoutDistances,
                distance => Assert.InRange(distance, -1, 1));
            Assert.Equal(
                transcript.Extent.Height - transcript.Viewport.Height,
                transcript.Offset.Y,
                precision: 3);
            AssertTranscriptTailGeometry(view, viewModel);

        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_RestoredRunningMcpToolKeepsPersistentStatusRow()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Restored MCP profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Restored MCP workspace");
        var session = services.SessionService.CreateSession(
            "Restored MCP session",
            workspaceId: workspace.WorkspaceId);
        var runRevision = services.SessionService.GetNextRunRevision(session.SessionId);
        services.SessionService.SaveCheckpoint(
            session.SessionId,
            runRevision,
            AgentRunStatus.Running,
            "Executing tool 'MCP'.");
        services.SessionService.AppendToolCallTurn(
            session.SessionId,
            AgentMessageRole.Assistant,
            "restored-mcp-call",
            McpToolIdentity.CreateStable("stitch", "edit_screens"),
            "{}");

        using var view = CreateAgentChatView(scope, services, profileService);
        var window = new Window { Width = 800, Height = 420, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>()));
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentActivityGroupRowViewModel>()
                .Any(row => row.IsRunning));

            Assert.True(viewModel.RunActivityRow.IsVisible);
            Assert.Single(viewModel.Messages.OfType<AgentActivityGroupRowViewModel>());
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_ToolCallAndResultRemainPinnedWithoutCollectionRemoval()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        await profileService.CreateProfileAsync("Tool tail profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Tool tail workspace");
        var session = services.SessionService.CreateSession(
            "Tool tail session",
            workspaceId: workspace.WorkspaceId);
        for (var index = 0; index < 8; index++)
        {
            services.SessionService.AppendTextTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                $"Existing response {index}: {new string('x', 100)}");
        }
        var runRevision = services.SessionService.GetNextRunRevision(session.SessionId);
        services.SessionService.SaveCheckpoint(
            session.SessionId,
            runRevision,
            AgentRunStatus.Running,
            "Thinking.");

        using var view = CreateAgentChatView(scope, services, profileService);
        Assert.IsType<AgentChatViewModel>(view.DataContext).IsDetailedTranscript = true;
        var window = new Window { Width = 800, Height = 360, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>()));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            var transcript = GetTranscriptScrollViewer(view);
            var repeater = Assert.IsType<ItemsRepeater>(
                view.FindControl<ItemsRepeater>("TranscriptItemsControl"));
            var completedLayoutDistances = new List<double>();
            var collectionRemovals = 0;
            var observeLayouts = false;
            transcript.LayoutUpdated += (_, _) =>
            {
                if (observeLayouts && transcript.Viewport.Height > 0)
                {
                    completedLayoutDistances.Add(
                        transcript.Extent.Height - transcript.Viewport.Height - transcript.Offset.Y);
                }
            };
            viewModel.Messages.CollectionChanged += (_, eventArgs) =>
            {
                if (eventArgs.Action == System.Collections.Specialized.NotifyCollectionChangedAction.Remove)
                {
                    collectionRemovals++;
                }
            };
            Assert.True(viewModel.RunActivityRow.IsVisible);
            AssertTranscriptTailGeometry(view, viewModel);

            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Reasoning,
                "Inspecting a longer reasoning activity update that changes the persistent row height.");
            await WaitUntilAsync(() => viewModel.RunActivityRow.ReasoningText.Contains(
                "Inspecting",
                StringComparison.Ordinal));
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            AssertTranscriptTailGeometry(view, viewModel);

            var largeDetailArguments = JsonSerializer.Serialize(new
            {
                detail = string.Join('\n', Enumerable.Range(0, 80).Select(index => $"Detail line {index}")),
            });
            var largeOutput = string.Join(
                '\n',
                Enumerable.Range(0, 100).Select(index => $"Output line {index}"));
            observeLayouts = true;
            services.SessionService.AppendToolCallTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                "tail-call",
                "test_tool",
                largeDetailArguments);
            await WaitUntilAsync(() => viewModel.Messages.OfType<AgentToolInvocationRowViewModel>().Any());
            Assert.True(viewModel.RunActivityRow.IsVisible);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            observeLayouts = false;

            Assert.Equal(0, collectionRemovals);
            Assert.NotEmpty(completedLayoutDistances);
            Assert.All(completedLayoutDistances, distance => Assert.InRange(distance, -1, 1));
            AssertTranscriptTailGeometry(view, viewModel);

            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Tool,
                "Running test tool");
            Assert.True(viewModel.RunActivityRow.IsVisible);
            completedLayoutDistances.Clear();
            observeLayouts = true;
            services.SessionService.AppendToolResultTurn(
                session.SessionId,
                "tail-call",
                "test_tool",
                largeDetailArguments,
                largeOutput,
                "Failed.",
                null,
                null,
                false,
                true,
                "test_failure",
                "test");
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentToolInvocationRowViewModel>()
                .Any(row => row.IsFailed && !row.IsExpanded));
            Assert.True(viewModel.RunActivityRow.IsVisible);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Background);
            observeLayouts = false;

            var failedTool = Assert.Single(
                viewModel.Messages.OfType<AgentToolInvocationRowViewModel>(),
                row => row.IsFailed);
            Assert.True(failedTool.HasDetails);
            Assert.False(failedTool.HasMaterializedDetails);
            Assert.Equal(0, collectionRemovals);
            Assert.NotEmpty(completedLayoutDistances);
            Assert.All(completedLayoutDistances, distance => Assert.InRange(distance, -1, 1));
            Assert.Equal(
                transcript.Extent.Height - transcript.Viewport.Height,
                transcript.Offset.Y,
                precision: 3);
            AssertTranscriptTailGeometry(view, viewModel);

            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Tool,
                "Running detached failure tool");
            await WaitUntilAsync(() => viewModel.RunActivityRow.IsVisible);
            services.SessionService.AppendToolCallTurn(
                session.SessionId,
                AgentMessageRole.Assistant,
                "detached-failure-call",
                "test_tool",
                largeDetailArguments);
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentToolInvocationRowViewModel>()
                .Count() == 2);
            await WaitForPendingCoordinatorOperationsAsync(view);
            services.SessionService.ReportRunActivity(
                session.SessionId,
                runRevision,
                AgentRunActivityKind.Tool,
                "Collecting detached failure output");
            Assert.True(viewModel.RunActivityRow.IsVisible);

            window.MouseMove(new Point(400, 180), RawInputModifiers.None);
            window.MouseWheel(new Point(400, 180), new Vector(0, 3), RawInputModifiers.None);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Loaded);
            var detachedAnchor = Assert.IsAssignableFrom<Control>(transcript.CurrentAnchor);
            Assert.IsNotType<AgentTranscriptTailSentinelRowViewModel>(detachedAnchor.DataContext);
            var detachedAnchorKey = Assert.IsAssignableFrom<AgentTranscriptRowViewModel>(
                detachedAnchor.DataContext).AnchorKey;
            var detachedAnchorTop = Assert.IsType<Point>(
                detachedAnchor.TranslatePoint(default, transcript)).Y;

            services.SessionService.AppendToolResultTurn(
                session.SessionId,
                "detached-failure-call",
                "test_tool",
                largeDetailArguments,
                largeOutput,
                "Failed while detached.",
                null,
                null,
                false,
                true,
                "detached_failure",
                "test");
            await WaitUntilAsync(() => viewModel.Messages
                .OfType<AgentToolInvocationRowViewModel>()
                .Count(row => row.ResultTurnId is not null
                              && row.IsFailed
                              && !row.IsExpanded
                              && !row.HasMaterializedDetails) == 2);
            await WaitForPendingCoordinatorOperationsAsync(view);
            await Dispatcher.UIThread.InvokeAsync(window.UpdateLayout, DispatcherPriority.Render);

            var restoredAnchorIndex = viewModel.Messages
                .Select((row, index) => (row.AnchorKey, Index: index))
                .Single(item => Equals(item.AnchorKey, detachedAnchorKey))
                .Index;
            var restoredAnchor = Assert.IsAssignableFrom<Control>(repeater.TryGetElement(restoredAnchorIndex));
            Assert.InRange(
                Math.Abs(Assert.IsType<Point>(restoredAnchor.TranslatePoint(default, transcript)).Y
                         - detachedAnchorTop),
                0,
                1);
            Assert.False(viewModel.RunActivityRow.IsVisible);
            Assert.False(GetAnchorHostSuspended(view));
        }
        finally
        {
            window.Close();
        }
    }
}
