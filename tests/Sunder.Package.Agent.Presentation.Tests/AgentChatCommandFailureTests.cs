extern alias AgentApp;

using AgentApp::Sunder.Package.Agent.PackageViews;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Services;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Xunit;
using AgentChatSelectionStateService = AgentApp::Sunder.Package.Agent.Services.AgentChatSelectionStateService;
using AgentToolPresentationService = AgentApp::Sunder.Package.Agent.Services.AgentToolPresentationService;
using static Sunder.Package.Agent.Presentation.Tests.RenderedChatFixture;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewComposition;
using static Sunder.Package.Agent.Presentation.Tests.RenderedViewWaits;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class AgentChatCommandFailureTests
{
    [AvaloniaFact]
    public async Task AgentChatViewModel_SendCommandContainsExpectedProviderFailure()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var provider = new ReadyChatProvider();
        services.ExtensionCatalog.AddProvider(RegressionTestBindings.ChatProviders, provider);
        var profile = await profileService.CreateProfileAsync("Fault containment profile");
        profileService.SaveProfile(
            profile.ProfileId,
            profile.DisplayName,
            profile.Description,
            profile.Instructions,
            provider.Descriptor.ProviderId,
            "test-model",
            embeddingProviderId: null,
            embeddingModelId: null);
        var workspace = services.WorkspaceService.CreateWorkspace("Fault containment workspace");
        var session = services.SessionService.CreateSession(
            "Fault containment session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var notifications = new RecordingNotificationService();
        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            ProviderFaultingRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            null!,
            new AgentAttachmentService(scope.Context),
            notifications,
            scope.Context);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>()));
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            viewModel.SelectedSession = viewModel.Sessions.Single(item => item.SessionId == session.SessionId);
            viewModel.DraftMessage = "message that the provider rejects";

            viewModel.SendMessageCommand.Execute(null);
            await WaitUntilAsync(() => notifications.Requests.Count == 1);
            await WaitUntilAsync(() => viewModel.SendMessageCommand.CanExecute(null));

            var notification = Assert.Single(notifications.Requests);
            Assert.Equal("Operation not completed", notification.Title);
            Assert.Contains("message could not be sent", notification.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal("message that the provider rejects", viewModel.DraftMessage);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task AgentChatView_PasteAndDropFailuresAreContainedByNamedOperations()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = CreateAgentServices(scope);
        using var profileService = services.ProfileService;
        var profile = await profileService.CreateProfileAsync("Attachment fault profile");
        var workspace = services.WorkspaceService.CreateWorkspace("Attachment fault workspace");
        var session = services.SessionService.CreateSession(
            "Attachment fault session",
            profileId: profile.ProfileId,
            workspaceId: workspace.WorkspaceId);
        var notifications = new RecordingNotificationService();
        using var view = RegressionTestAppViews.ChatView(
            profileService,
            services.WorkspaceService,
            services.SessionService,
            NoOpPermissionGateway.Instance,
            NoOpRunGateway.Instance,
            new AgentChatSelectionStateService(scope.Context),
            new AgentToolPresentationService(),
            new AgentExecutionTargetWarmupService(
                services.WorkspaceService,
                services.TargetService),
            null!,
            ProviderFaultingAttachmentGateway.Instance,
            notifications,
            scope.Context);
        var window = new Window { Width = 900, Height = 700, Content = view };
        window.Show();
        try
        {
            await view.OnNavigatedToAsync(new PackageViewNavigationContext(
                "younics.ai.agent.chat",
                new Dictionary<string, string?>()));
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            viewModel.SelectedSession = viewModel.Sessions.Single(item => item.SessionId == session.SessionId);

            view.SubmitPastedAttachmentPaths(["pasted.txt"]);
            view.SubmitDroppedAttachmentPaths(["dropped.txt"]);
            await WaitUntilAsync(() => notifications.Requests.Count == 2);

            Assert.Contains(
                notifications.Requests,
                request => request.Message.Contains("clipboard content", StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                notifications.Requests,
                request => request.Message.Contains("dropped files", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(viewModel.PendingAttachments);
        }
        finally
        {
            window.Close();
        }
    }

    private sealed class ProviderFaultingRunGateway : IAgentRunGateway, IAgentCorrelatedRunGateway
    {
        public static ProviderFaultingRunGateway Instance { get; } = new();

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(Guid sessionId, string profileId, string userMessage,
            string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments, Guid userTurnId,
            CancellationToken cancellationToken = default) => Task.FromException<AgentRunCheckpointRecord>(ProviderFailure());

        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(Guid sessionId, Guid rollbackAnchorTurnId,
            string profileId, string userMessage, string workspaceId, IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            Guid userTurnId, CancellationToken cancellationToken = default) => Task.FromException<AgentRunCheckpointRecord>(ProviderFailure());

        public Task<AgentRunCheckpointRecord> QueueUserMessageAsync(
            Guid sessionId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentRunCheckpointRecord>(ProviderFailure());

        public Task<AgentRunCheckpointRecord> RollbackAndQueueUserMessageAsync(
            Guid sessionId,
            Guid rollbackAnchorTurnId,
            string profileId,
            string userMessage,
            string workspaceId,
            IReadOnlyList<AgentAttachmentUploadRequest> attachments,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentRunCheckpointRecord>(ProviderFailure());

        public Task<AgentRunCheckpointRecord?> StopAsync(
            Guid sessionId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> ApprovePendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);

        public Task<AgentRunCheckpointRecord?> DenyPendingPermissionAsync(
            Guid sessionId,
            string requestId,
            CancellationToken cancellationToken = default)
            => Task.FromResult<AgentRunCheckpointRecord?>(null);
    }

    private sealed class ProviderFaultingAttachmentGateway : IAgentAttachmentGateway
    {
        public static ProviderFaultingAttachmentGateway Instance { get; } = new();

        public Task<AgentAttachmentUploadRequest> LoadUploadRequestFromFileAsync(
            string path,
            CancellationToken cancellationToken = default)
            => Task.FromException<AgentAttachmentUploadRequest>(ProviderFailure());

        public AgentAttachmentInfo InspectUpload(AgentAttachmentUploadRequest upload)
            => throw new NotSupportedException();

        public Task<byte[]> ReadAttachmentBytesAsync(
            AgentAttachmentMetadata metadata,
            CancellationToken cancellationToken = default)
            => Task.FromException<byte[]>(ProviderFailure());
    }

    private sealed class ReadyChatProvider : IAgentChatProvider
    {
        public AgentProviderDescriptor Descriptor { get; } = new(
            "fault-containment-provider",
            "Fault Containment Provider",
            [],
            SupportsStreaming: true,
            SupportsInterruptibleRuns: true);

        public ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult<IReadOnlyList<AgentModelDescriptor>>(
                [new AgentModelDescriptor("test-model", "Test Model", 16_000, 2_000)]);

        public ValueTask<AgentProviderReadiness> GetReadinessAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new AgentProviderReadiness(
                Descriptor.ProviderId,
                AgentProviderReadinessStatus.Ready,
                "Ready."));

        public ValueTask<AgentProviderRunCapabilities> GetRunCapabilitiesAsync(
            string? modelId,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public ValueTask<IChatClient> CreateChatClientAsync(
            AgentChatClientContext context,
            CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class RecordingNotificationService : IPackageNotificationService
    {
        private readonly object _syncRoot = new();
        private readonly List<PackageNotificationRequest> _requests = [];

        public IReadOnlyList<PackageNotificationRequest> Requests
        {
            get
            {
                lock (_syncRoot)
                {
                    return _requests.ToArray();
                }
            }
        }

        public ValueTask PublishAsync(
            PackageNotificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            lock (_syncRoot)
            {
                _requests.Add(request);
            }

            return ValueTask.CompletedTask;
        }
    }

    private static SunderRpcException ProviderFailure()
        => new(new SunderRpcError(
            SunderRpcErrorKind.ProviderFaulted,
            "test.provider-faulted",
            "Injected provider failure."));
}
