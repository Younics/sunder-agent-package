extern alias AgentCore;
using AgentCore::Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Fact]
    public async Task AgentChat_ContinueIsANewSubmissionAndNeverOverwritesADraft()
    {
        var provider = new ScriptedProvider((_, _) => Complete("continued"));
        var tool = new TestTool("test-read");
        using var runtime = AgentTestRuntime.Create(provider, tool);
        var sessionId = await runtime.CreateSessionAsync("test-read");
        var revision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(sessionId, revision, AgentRunStatus.Interrupted, "Provider recovery exhausted.");
        using var viewModel = RegressionTestAppViews.Chat(runtime.ProfileService, runtime.WorkspaceService,
            runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        await viewModel.InitializeAsync();
        Assert.True(viewModel.CanContinueRun);
        viewModel.DraftMessage = "My unsent instructions";
        Assert.False(viewModel.CanContinueRun);
        await viewModel.ContinueRunCommand.ExecuteAsync(null);
        Assert.Equal("My unsent instructions", viewModel.DraftMessage);
        Assert.Empty(provider.Requests);
        viewModel.DraftMessage = string.Empty;
        Assert.True(viewModel.ContinueRunCommand.CanExecute(null));
        await viewModel.ContinueRunCommand.ExecuteAsync(null);
        await WaitUntilAsync(() => runtime.SessionService.GetLatestCheckpoint(sessionId)?.Status == AgentRunStatus.Completed);
        var user = Assert.Single(runtime.SessionService.ListTurns(sessionId), turn => turn.Role == AgentMessageRole.User);
        Assert.StartsWith("Continue from the saved progress", RenderTurnText(user));
        Assert.Single(provider.Requests);
        Assert.Equal(0, tool.ExecutionCount);
    }

    [Fact]
    public async Task RunActivity_QueuedPublicationRechecksTheOwningRun()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("not invoked")));
        var sessionId = await runtime.CreateSessionAsync("noop");
        var revision = runtime.SessionService.GetNextRunRevision(sessionId);
        var current = true;
        var delivered = 0;
        runtime.SessionService.RunActivityChanged += (_, _) => delivered++;
        runtime.SessionService.SessionChanged += id =>
        {
            if (id != sessionId || !current) return;
            runtime.SessionService.ReportRunActivity(id, revision, AgentRunActivityKind.Retrying, "Old owner",
                new AgentRunRetryState(2, DateTimeOffset.UtcNow.AddMinutes(1), "Old owner", false), () => current);
            current = false;
        };
        runtime.SessionService.SaveCheckpoint(sessionId, revision, AgentRunStatus.Running, "Thinking.");
        Assert.Equal(0, delivered);
        Assert.Null(runtime.SessionService.GetRunActivity(sessionId));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentChat_RestoresRetryDeadlineAndReasoningWithoutReplayingWork(bool detailed)
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("not invoked")));
        var sessionId = await runtime.CreateSessionAsync("noop");
        var revision = runtime.SessionService.GetNextRunRevision(sessionId);
        runtime.SessionService.SaveCheckpoint(sessionId, revision, AgentRunStatus.Running, "Thinking.");
        runtime.SessionService.ReportRunActivity(sessionId, revision, AgentRunActivityKind.Reasoning, "Reviewing saved work.");
        var retry = new AgentRunRetryState(4, DateTimeOffset.UtcNow.AddMinutes(3), "Servers overloaded", true);
        runtime.SessionService.ReportRunActivity(sessionId, revision, AgentRunActivityKind.Retrying, retry.Reason, retry);
        using var viewModel = RegressionTestAppViews.Chat(runtime.ProfileService, runtime.WorkspaceService,
            runtime.SessionService, runtime.PermissionService, runtime.RunCoordinator);
        viewModel.IsDetailedTranscript = detailed;
        await viewModel.InitializeAsync();
        var row = viewModel.RunActivityRow;
        Assert.True(row.IsVisible);
        Assert.Contains("Servers overloaded", row.ThinkingText);
        Assert.Contains("continuing in", row.ThinkingText);
        Assert.Contains("attempt 4", row.ThinkingText);
        Assert.Equal("Reviewing saved work.", row.ReasoningText);
        Assert.Same(row, viewModel.TranscriptItems[^2]);
        runtime.SessionService.ReportRunActivity(sessionId, revision, AgentRunActivityKind.Tool, "Running tools");
        Assert.True(row.IsVisible);
        Assert.StartsWith("Running tools", row.ThinkingText);
        Assert.Equal("Reviewing saved work.", row.ReasoningText);
        runtime.SessionService.SaveCheckpoint(sessionId, revision, AgentRunStatus.Stopped, "Stopped.");
        runtime.SessionService.ReportRunActivity(sessionId, revision, AgentRunActivityKind.Retrying, retry.Reason, retry);
        Assert.False(row.IsVisible);
        Assert.Null(runtime.SessionService.GetRunActivity(sessionId));
    }
}
