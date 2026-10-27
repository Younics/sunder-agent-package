extern alias AgentCore;

using System.Reflection;
using Sunder.Package.Agent.Services;
using Xunit;
using AppRuntime = AgentCore::Sunder.Package.Agent.Runtime;
using AgentChatViewModel = AgentCore::Sunder.Package.Agent.PackageViews.AgentChatViewModel;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Fact]
    public async Task SendReservesBeforeAwaitAndNeverReadsHistoryToRecognizeItsUserTurn()
    {
        using var runtime = AgentTestRuntime.Create(new ScriptedProvider((_, _) => Complete("unused")));
        var session = await runtime.CreateSessionAsync("noop");
        var gateway = new PendingAdmissionRunGateway(runtime.Store, holdResponse: true);
        using var view = RegressionTestAppViews.Chat(runtime.ProfileService, runtime.WorkspaceService,
            runtime.SessionService, runtime.PermissionService, gateway);
        await view.InitializeAsync();
        typeof(AgentChatViewModel).GetField("_transcriptPageGateway", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(view, new NoSendHistoryReads());
        var followRequests = 0;
        view.TranscriptTailFollowRequested += _ => followRequests++;
        view.DraftMessage = "send once";

        var first = view.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(1, gateway.CommandCount);
        Assert.False(first.IsCompleted);
        Assert.False(view.SendMessageCommand.CanExecute(null));
        await view.SendMessageCommand.ExecuteAsync(null);
        await view.SendMessageCommand.ExecuteAsync(null);
        Assert.Equal(1, gateway.CommandCount);
        Assert.Equal(1, followRequests);

        view.DraftMessage = "a new draft while sending";
        view.DraftMessage = "send once"; // An edit back to identical text is still a newer draft.
        Assert.True(view.DetachTranscriptFromLatest());
        gateway.CommitPendingAdmission();
        gateway.ReleaseResponse.TrySetResult();
        await first.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("send once", view.DraftMessage);
        Assert.False(view.IsTranscriptFollowingLatest);
        Assert.Equal(session, gateway.SessionId);
        Assert.Equal(1, followRequests);
    }

    private sealed class NoSendHistoryReads : AppRuntime.IAgentTranscriptPageGateway
    {
        public Task<AppRuntime.AgentTranscriptPage> LoadTranscriptPageAsync(
            AppRuntime.AgentTranscriptPageRequest request, CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Send must use the exact admission identity, not read history.");
    }
}
