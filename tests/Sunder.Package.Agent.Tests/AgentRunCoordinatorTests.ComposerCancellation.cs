extern alias AgentCore;

using System.Reflection;
using AgentCore::Sunder.Package.Agent.PackageViews;
using AgentCore::Sunder.Package.Agent.Runtime;
using AgentCore::Sunder.Package.Agent.Shared.Presentation;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentRunCoordinatorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AgentChatViewModel_DisposingReconciliationContainsOnlyItsOwnCancellation(bool foreignCancellation)
    {
        using var runtime = AgentTestRuntime.Create(
            new ScriptedProvider((_, _) => Complete("unused")));
        var sessionId = await runtime.CreateSessionAsync("noop");
        using var viewModel = RegressionTestAppViews.Chat(
            runtime.ProfileService, runtime.WorkspaceService, runtime.SessionService,
            runtime.PermissionService, runtime.RunCoordinator, InlineAgentPresentationDispatcher.Instance);
        await viewModel.InitializeAsync();

        using var foreign = new CancellationTokenSource();
        foreign.Cancel();
        var gateway = new PausedComposerTranscriptRead(foreignCancellation ? foreign.Token : default);
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic;
        var type = typeof(AgentChatViewModel);
        type.GetField("_transcriptPageGateway", members)!.SetValue(viewModel, gateway);
        var composer = Assert.IsType<AgentComposerState>(type.GetField("_composer", members)!.GetValue(viewModel));
        var scope = Assert.IsType<PresentationTaskScope>(type.GetField("_backgroundTasks", members)!.GetValue(viewModel));
        viewModel.DraftMessage = "awaiting an admission verdict";
        var submission = Assert.IsType<AgentComposerSubmission>(composer.TryBeginSubmission(sessionId));
        Assert.False(submission.CompleteCommand());
        viewModel.DraftMessage = "newer draft";

        // Use the actual observation boundary, but await its verdict so a
        // regression fails this test rather than crashing the test host.
        var reconcile = type.GetMethod("ReconcileCompletedComposerSubmissionsAsync", members)!
            .CreateDelegate<Func<CancellationToken, Task>>(viewModel);
        var observed = scope.RunAsync(reconcile);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            var (request, token) = await gateway.Entered.Task.WaitAsync(deadline.Token);
            Assert.Equal(sessionId, request.SessionId);
            Assert.Equal(submission.UserTurnId, request.AnchorTurnId);
            Assert.Equal(AgentTranscriptPageDirection.Turn, request.Direction);
            Assert.False(observed.IsCompleted);

            viewModel.Dispose();
            Assert.True(scope.CancellationToken.IsCancellationRequested);
            gateway.Release.TrySetResult();
            if (foreignCancellation)
            {
                var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(
                    () => observed.WaitAsync(deadline.Token));
                Assert.Equal(foreign.Token, failure.CancellationToken);
            }
            else
            {
                await observed.WaitAsync(deadline.Token);
                Assert.Equal(scope.CancellationToken, token);
            }

            Assert.False(submission.IsCommitted);
            Assert.Equal("newer draft", viewModel.DraftMessage);
        }
        finally
        {
            viewModel.Dispose();
            gateway.Release.TrySetResult();
            await scope.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class PausedComposerTranscriptRead(CancellationToken foreignToken) : IAgentTranscriptPageGateway
    {
        public TaskCompletionSource<(AgentTranscriptPageRequest, CancellationToken)> Entered { get; }
            = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AgentTranscriptPage> LoadTranscriptPageAsync(
            AgentTranscriptPageRequest request, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult((request, cancellationToken));
            await Release.Task.ConfigureAwait(false);
            foreignToken.ThrowIfCancellationRequested();
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The parked transcript read must be cancelled before release.");
        }
    }
}
