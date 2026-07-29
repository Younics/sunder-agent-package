using System.Net;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Tests;
using Sunder.Runtime.Client;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class CrossRepositoryCandidatePreparationTests
{
    [AvaloniaFact]
    public async Task SelectedAgentChat_RecoversWhenCandidateRuntimeBecomesLeaseReady()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var handler = new AgentRuntimeHandler();
        using var transport = new RuntimePackageOperationClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        var publication = new AppPackageGenerationPublication();
        var runtime = new AppPackageRuntimeClient("sunder.package.agent", transport, publication);
        var services = CreateAppServices(packageScope, runtime);
        await using var provider = services.BuildServiceProvider();
        AgentChatView? view = null;
        Window? window = null;
        var context = new PackageViewNavigationContext(
            "sunder.package.agent.chat",
            new Dictionary<string, string?>());
        try
        {
            Assert.False(runtime.IsAvailable);
            Assert.False(publication.IsPublished);
            using (publication.BeginRuntimePreparation())
            {
                view = ActivatorUtilities.CreateInstance<AgentChatView>(provider);
                var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
                window = new Window { Width = 900, Height = 700, Content = view };
                window.Show();

                Assert.False(await view.PrepareNavigationAsync(context));

                Assert.Equal(1, handler.ChatSnapshotRequestCount);
                Assert.Equal(0, handler.SuccessfulChatSnapshotRequestCount);
                Assert.Contains("Reconnecting", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
                Assert.NotEqual("Unable to load Agent Chat", viewModel.SetupTitle);
                Assert.False(publication.IsPublished);
            }

            Assert.False(runtime.IsAvailable);
            publication.Publish();
            Assert.True(runtime.IsAvailable);
            handler.EnableLeases();

            await handler.SuccessfulChatSnapshot.Task.WaitAsync(TimeSpan.FromSeconds(3));
            await WaitUntilAsync(() => !Assert.IsType<AgentChatViewModel>(view.DataContext)
                .StatusText.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase));

            var initializedViewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            Assert.Equal(2, handler.ChatSnapshotRequestCount);
            Assert.Equal(1, handler.SuccessfulChatSnapshotRequestCount);
            Assert.Equal("Create an agent before chatting", initializedViewModel.SetupTitle);
            Assert.DoesNotContain("Reconnecting", initializedViewModel.StatusText, StringComparison.OrdinalIgnoreCase);

            publication.Revoke();
            var retired = await Assert.ThrowsAsync<PackageRuntimeInvocationException>(async () =>
                await runtime.InvokeAsync(
                    new PackageRuntimeOperation<EchoRequest, EchoResponse>("test.retired.v1"),
                    new EchoRequest("retired")));
            Assert.Equal("runtime.v1.unavailable", retired.Code);
            Assert.Equal(0, handler.RetiredRequestCount);
        }
        finally
        {
            publication.Revoke();
            window?.Close();
            view?.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for Agent Chat candidate startup recovery.");
            }

            await Task.Delay(10);
        }
    }

    private static ServiceCollection CreateAppServices(
        RegressionTestPackageScope packageScope,
        IPackageRuntimeClient runtime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(packageScope.Context);
        services.AddSingleton(runtime);
        services.AddSingleton<IPackageRuntimeClient>(runtime);
        services.AddSingleton<IPackageExtensionCatalog>(new RegressionTestExtensionCatalog());
        services.AddSingleton<IPackageShellViewService, NoOpPackageShellViewService>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundProcessQueue, NoOpBackgroundProcessQueue>();
        new Sunder.Package.Agent.AppPackageModule().ConfigureAppServices(services, packageScope.Context);
        return services;
    }

    private sealed record EchoRequest(string Value);

    private sealed record EchoResponse(string Value);

    private sealed class AgentRuntimeHandler : HttpMessageHandler
    {
        private int _chatSnapshotRequestCount;
        private int _successfulChatSnapshotRequestCount;
        private int _retiredRequestCount;
        private int _leasesEnabled;

        public int ChatSnapshotRequestCount => Volatile.Read(ref _chatSnapshotRequestCount);

        public int SuccessfulChatSnapshotRequestCount
            => Volatile.Read(ref _successfulChatSnapshotRequestCount);

        public int RetiredRequestCount => Volatile.Read(ref _retiredRequestCount);

        public TaskCompletionSource SuccessfulChatSnapshot { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnableLeases() => Volatile.Write(ref _leasesEnabled, 1);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (request.RequestUri?.AbsolutePath == "/api/handshake")
            {
                return Task.FromResult(JsonResponse(
                    "{\"protocolIdentity\":\"dev.sunder.runtime\",\"protocolRevision\":3,\"minimumSupportedRevision\":3,\"maximumSupportedRevision\":3,\"runtimeInstanceId\":\"11111111-1111-1111-1111-111111111111\",\"supportedFeatures\":[\"api.v1\",\"package-runtime-operations.v1\",\"package-runtime-stream-envelopes.v1\"],\"product\":{\"productName\":\"Sunder.Runtime.Host\",\"productVersion\":\"Development\",\"informationalVersion\":\"Development\"}}"));
            }

            return request.RequestUri?.AbsolutePath switch
            {
                "/api/v1/packages/sunder.package.agent/operations/agent.chat.snapshot.v1" =>
                    Task.FromResult(ChatSnapshotResponse()),
                "/api/v1/packages/sunder.package.agent/streams/agent.changes.v1" =>
                    Task.FromResult(ChangeStreamResponse()),
                "/api/v1/packages/sunder.package.agent/operations/test.retired.v1" =>
                    Task.FromResult(RetiredResponse()),
                _ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound)),
            };
        }

        private HttpResponseMessage RetiredResponse()
        {
            Interlocked.Increment(ref _retiredRequestCount);
            return JsonResponse("{\"value\":\"unexpected\"}");
        }

        private HttpResponseMessage ChatSnapshotResponse()
        {
            Interlocked.Increment(ref _chatSnapshotRequestCount);
            if (Volatile.Read(ref _leasesEnabled) == 0)
            {
                return UnavailableResponse();
            }

            Interlocked.Increment(ref _successfulChatSnapshotRequestCount);
            SuccessfulChatSnapshot.TrySetResult();
            return JsonResponse(
                """
                {
                  "revision":1,
                  "profiles":[],
                  "workspaces":[],
                  "workspaceBindings":[],
                  "selectedProfile":null,
                  "selectedWorkspace":null,
                  "selectedSession":null,
                  "workspaceSessions":[],
                  "initialTranscript":{"revision":1,"turns":[],"hasMore":false,"continuation":null},
                  "permissions":{"revision":1,"sessionState":null,"pendingRequests":[]},
                  "runtimeInstanceId":"candidate-runtime"
                }
                """);
        }

        private HttpResponseMessage ChangeStreamResponse()
            => Volatile.Read(ref _leasesEnabled) == 0
                ? UnavailableResponse()
                : JsonResponse(
                    "{\"type\":\"event\",\"event\":{\"revision\":1,\"kind\":0,\"runtimeInstanceId\":\"candidate-runtime\"}}\n{\"type\":\"completed\"}\n",
                    "application/x-ndjson");

        private static HttpResponseMessage UnavailableResponse()
            => new(HttpStatusCode.ServiceUnavailable)
            {
                Content = new StringContent(
                    "{\"title\":\"Runtime unavailable\",\"detail\":\"The candidate Runtime generation is not lease-ready.\",\"code\":\"runtime.v1.unavailable\"}",
                    Encoding.UTF8,
                    "application/problem+json"),
            };

        private static HttpResponseMessage JsonResponse(
            string content,
            string mediaType = "application/json")
            => new(HttpStatusCode.OK)
            {
                Content = new StringContent(content, Encoding.UTF8, mediaType),
            };
    }

    private sealed class NoOpPackageShellViewService : IPackageShellViewService
    {
        public IReadOnlyList<PackageHotbarView> ListHotbarViews() => [];

        public bool IsViewInHotbar(string viewId) => false;

        public ValueTask<bool> AddViewToDefaultHotbarAsync(
            string viewId,
            bool openPanel = false,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> AddViewToHotbarAsync(
            string viewId,
            PackageViewPlacement placement,
            int? index = null,
            bool openPanel = false,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> RemoveViewFromHotbarAsync(
            string viewId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> OpenViewPanelAsync(
            string viewId,
            IReadOnlyDictionary<string, string?>? parameters = null,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> CloseViewPanelAsync(
            string viewId,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class NoOpBackgroundProcessQueue : IBackgroundProcessQueue
    {
        public event EventHandler<BackgroundProcessChangedEventArgs>? ProcessChanged
        {
            add { }
            remove { }
        }

        public BackgroundProcessSnapshot Enqueue(BackgroundProcessRequest request)
            => new(
                Guid.NewGuid(),
                request.Title,
                request.GroupKey,
                request.Indicator,
                request.ConcurrencyMode,
                BackgroundProcessState.Queued,
                string.Empty,
                ProgressPercent: null,
                request.CanCancel,
                request.Metadata ?? new Dictionary<string, string>(),
                ErrorMessage: null,
                DateTimeOffset.UtcNow,
                StartedAtUtc: null,
                CompletedAtUtc: null);

        public IReadOnlyList<BackgroundProcessSnapshot> ListProcesses(string? groupKey = null) => [];

        public bool Cancel(Guid processId) => false;
    }
}
