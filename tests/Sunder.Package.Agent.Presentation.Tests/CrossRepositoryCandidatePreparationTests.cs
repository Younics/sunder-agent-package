extern alias AgentApp;

using System.Net;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Sunder.App.Services;
using AgentApp::Sunder.Package.Agent.PackageViews;
using Sunder.Package.Agent.Protocol;
using AgentApp::Sunder.Package.Agent.Runtime;
using Sunder.Package.Agent.Tests;
using Sunder.Runtime.Client;
using Sunder.Runtime.Contracts;
using Sunder.Runtime.LocalState;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Xunit;
using OwnerRuntime = global::Sunder.Package.Agent.Runtime;

namespace Sunder.Package.Agent.Presentation.Tests;

public sealed class CrossRepositoryCandidatePreparationTests
{
    [AvaloniaFact]
    public async Task SelectedAgentChat_RecoversWhenCandidateOwnerProviderBecomesLeaseReady()
    {
        using var packageScope = RegressionTestPackageScope.Create();
        var handler = new AgentRpcRuntimeHandler();
        var management = new RuntimeManagementClient(
            () => new RuntimeConnectionInfo(new Uri("http://127.0.0.1:5199"), "test-token"),
            handler);
        await using var runtime = new RuntimeAppWebRpcClient(management, "candidate-app-session");
        var services = CreateAppServices(packageScope, runtime);
        await using var provider = services.BuildServiceProvider();
        var availability = Assert.IsAssignableFrom<IAgentRuntimeAvailability>(provider.GetRequiredService<AgentAppRuntimeGateway>());
        AgentChatView? view = null;
        Window? window = null;
        var context = new PackageViewNavigationContext(
            "younics.ai.agent.chat",
            new Dictionary<string, string?>());
        try
        {
            view = provider.GetRequiredService<AgentChatView>();
            var viewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            window = new Window { Width = 900, Height = 700, Content = view };
            window.Show();

            Assert.False(await view.PrepareNavigationAsync(context));

            await WaitUntilAsync(() => handler.OwnerDiscoveryRequestCount > 0,
                () => $"Requests: {string.Join(", ", handler.RequestPaths)}; status: {viewModel.StatusText}; setup: {viewModel.SetupTitle}");
            Assert.True(handler.OwnerDiscoveryRequestCount > 0);
            Assert.Equal(0, handler.ChatSnapshotRequestCount);
            Assert.Contains("Reconnecting", viewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.NotEqual("Unable to load Agent Chat", viewModel.SetupTitle);
            Assert.False(availability.IsRuntimeAvailable);

            handler.EnableLeases();

            await WaitUntilAsync(() => handler.SuccessfulChatSnapshot.Task.IsCompleted,
                () => $"Requests: {string.Join(", ", handler.RequestPaths)}; status: {viewModel.StatusText}; setup: {viewModel.SetupTitle}");
            await handler.SuccessfulChatSnapshot.Task;
            await WaitUntilAsync(() => !Assert.IsType<AgentChatViewModel>(view.DataContext)
                .StatusText.Contains("Reconnecting", StringComparison.OrdinalIgnoreCase));

            var initializedViewModel = Assert.IsType<AgentChatViewModel>(view.DataContext);
            Assert.Equal(1, handler.ChatSnapshotRequestCount);
            Assert.Equal("Create an agent before chatting", initializedViewModel.SetupTitle);
            Assert.DoesNotContain("Reconnecting", initializedViewModel.StatusText, StringComparison.OrdinalIgnoreCase);
            Assert.True(availability.IsRuntimeAvailable);

            handler.DisableLeases();
            var owner = provider.GetRequiredService<AgentAppRuntimeGateway>();
            var retired = await Assert.ThrowsAsync<SunderRpcException>(async () =>
                await owner.LoadChatSnapshotAsync(
                    new AgentChatSnapshotRequest(),
                    CancellationToken.None));
            Assert.Equal("agent.runtime.unavailable", retired.Error.Code);
            Assert.Equal(0, handler.RetiredRequestCount);
        }
        finally
        {
            window?.Close();
            view?.Dispose();
        }
    }

    private static async Task WaitUntilAsync(Func<bool> condition, Func<string>? diagnostic = null)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        while (!condition())
        {
            if (DateTimeOffset.UtcNow >= deadline)
            {
                throw new TimeoutException("Timed out waiting for Agent Chat candidate startup recovery. " + diagnostic?.Invoke());
            }

            await Task.Delay(10);
        }
    }

    private static ServiceCollection CreateAppServices(
        RegressionTestPackageScope packageScope,
        ISunderRpcClient runtime)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(packageScope.Context);
        services.AddSingleton(runtime);
        services.AddSingleton<ISunderRpcClient>(runtime);
        services.AddSingleton<IPackageShellViewService, NoOpPackageShellViewService>();
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IBackgroundProcessQueue, NoOpBackgroundProcessQueue>();
        new AgentApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, packageScope.Context);
        return services;
    }

    private sealed class AgentRpcRuntimeHandler : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);
        private static readonly Guid ActivationId = new("11111111-1111-1111-1111-111111111111");
        private int _ownerDiscoveryRequestCount;
        private int _chatSnapshotRequestCount;
        private int _retiredRequestCount;
        private int _leasesEnabled;
        public ConcurrentQueue<string> RequestPaths { get; } = new();

        public int OwnerDiscoveryRequestCount => Volatile.Read(ref _ownerDiscoveryRequestCount);

        public int ChatSnapshotRequestCount => Volatile.Read(ref _chatSnapshotRequestCount);

        public int RetiredRequestCount => Volatile.Read(ref _retiredRequestCount);

        public TaskCompletionSource SuccessfulChatSnapshot { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource LeasesEnabled { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void EnableLeases()
        {
            Volatile.Write(ref _leasesEnabled, 1);
            LeasesEnabled.TrySetResult();
        }

        public void DisableLeases() => Volatile.Write(ref _leasesEnabled, 0);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri?.AbsolutePath;
            RequestPaths.Enqueue(path ?? "<missing URI>");
            if (path == "/api/handshake")
            {
                return Json(new RuntimeHandshakeResponse(
                    RuntimeProtocol.Identity,
                    RuntimeProtocol.CurrentRevision,
                    RuntimeProtocol.MinimumSupportedRevision,
                    RuntimeProtocol.MaximumSupportedRevision,
                    ActivationId,
                    [RuntimeProtocolFeatures.VersionedApiV1, RuntimeProtocolFeatures.OfficialRuntimeDownloadsV1,
                        RuntimeProtocolFeatures.AppWebRpcV1],
                    new RuntimeProductVersionDiagnostics(
                        "Sunder.Runtime.Host",
                        "Development",
                        "Development")));
            }

            switch (path)
            {
                case "/api/v1/rpc/app/discover":
                    return await DiscoverAsync(request, cancellationToken);
                case "/api/v1/rpc/app/provider":
                    var lookup = await request.Content!.ReadFromJsonAsync<RuntimeRpcAppProviderRequest>(
                        WebJson, cancellationToken);
                    Assert.Equal("rpc1_agent_owner", lookup?.EndpointReference);
                    return Json(new RuntimeRpcAppProviderResponse(
                        Volatile.Read(ref _leasesEnabled) == 0 ? null : CreateOwnerProvider(),
                        Error: null));
                case "/api/v1/rpc/app/call-scopes/open":
                    return Json(new RuntimeRpcAppCallScopeDescriptor(
                        $"scope-{Guid.NewGuid():N}",
                        DateTimeOffset.UtcNow.AddMinutes(1)));
                case "/api/v1/rpc/app/call-scopes/close":
                case "/api/v1/rpc/app-sessions/close":
                    return new HttpResponseMessage(HttpStatusCode.NoContent);
                case "/api/v1/rpc/app/invoke":
                    return await InvokeAsync(request, cancellationToken);
                case "/api/v1/rpc/app/subscribe":
                    return await SubscribeAsync(request, cancellationToken);
                case "/api/v1/rpc/app/watch":
                    return await WatchAsync(request, cancellationToken);
                default:
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
            }
        }

        private async Task<HttpResponseMessage> DiscoverAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var discover = await request.Content!.ReadFromJsonAsync<RuntimeRpcAppDiscoverRequest>(
                WebJson,
                cancellationToken);
            Assert.Equal("younics.ai.agent.owner.runtime", discover?.ContractId);
            Interlocked.Increment(ref _ownerDiscoveryRequestCount);
            RuntimeRpcProviderDescriptor[] providers = Volatile.Read(ref _leasesEnabled) == 0
                ? Array.Empty<RuntimeRpcProviderDescriptor>()
                : [CreateOwnerProvider()];
            return Json(new RuntimeRpcAppDiscoverResponse(
                new RuntimeRpcCatalogSnapshot(providers.Length == 0 ? 1 : 2, providers.Length == 0 ? 1 : 2,
                    providers, ResetRequired: false),
                Error: null));
        }

        private async Task<HttpResponseMessage> InvokeAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var invocation = await request.Content!.ReadFromJsonAsync<RuntimeRpcAppInvokeRequest>(
                WebJson,
                cancellationToken)
                ?? throw new InvalidOperationException("The RPC invocation request was empty.");
            RequestPaths.Enqueue("invoke:" + invocation.MethodId);
            if (Volatile.Read(ref _leasesEnabled) == 0)
            {
                Interlocked.Increment(ref _retiredRequestCount);
                return Json(new RuntimeRpcAppInvokeResponse(Value: null,
                    new RuntimeRpcErrorDescriptor(RuntimeRpcErrorKind.StaleEndpoint,
                        "test.retired-provider", "The owner provider has retired.")));
            }
            if (invocation.MethodId == "transport-profile")
            {
                return Json(new RuntimeRpcAppInvokeResponse(
                    JsonSerializer.SerializeToElement(OwnerRuntime.AgentTransportProfile.StandardFallback,
                        CurrentPrivateRpcDescriptors.OwnerJson),
                    Error: null));
            }

            if (invocation.MethodId == "dashboard")
            {
                return Json(new RuntimeRpcAppInvokeResponse(
                    JsonSerializer.SerializeToElement(new OwnerRuntime.AgentDashboardProjection(1, [], [], []),
                        CurrentPrivateRpcDescriptors.OwnerJson),
                    Error: null));
            }

            if (invocation.MethodId == "chat-snapshot")
            {
                Interlocked.Increment(ref _chatSnapshotRequestCount);
                SuccessfulChatSnapshot.TrySetResult();
                return Json(new RuntimeRpcAppInvokeResponse(
                    JsonSerializer.SerializeToElement(new OwnerRuntime.AgentChatSnapshotProjection(
                        1, [], [], [], null, null, null, [], new(1, [], false), new(1, null, []),
                        RuntimeInstanceId: "candidate-runtime"), CurrentPrivateRpcDescriptors.OwnerJson),
                    Error: null));
            }

            return Json(new RuntimeRpcAppInvokeResponse(
                Value: null,
                new RuntimeRpcErrorDescriptor(
                    RuntimeRpcErrorKind.NotFound,
                    "test.method-not-found",
                    "The test method was not found.")));
        }

        private async Task<HttpResponseMessage> SubscribeAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var subscription = await request.Content!.ReadFromJsonAsync<RuntimeRpcAppInvokeRequest>(
                WebJson,
                cancellationToken)
                ?? throw new InvalidOperationException("The RPC subscription request was empty.");
            Assert.Equal("changes", subscription.MethodId);
            var frame = new RuntimeRpcAppSubscriptionFrame(
                RuntimeRpcAppStreamFrameTypes.Event,
                JsonSerializer.SerializeToElement(new OwnerRuntime.AgentRuntimeChange(
                    1, OwnerRuntime.AgentRuntimeChangeKind.Connected, RuntimeInstanceId: "candidate-runtime"),
                    CurrentPrivateRpcDescriptors.OwnerJson),
                Error: null);
            var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, WebJson) + "\n");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new BlockingEventStream(payload))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/x-ndjson") },
                },
            };
        }

        private async Task<HttpResponseMessage> WatchAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var watch = await request.Content!.ReadFromJsonAsync<RuntimeRpcAppWatchRequest>(
                WebJson,
                cancellationToken)
                ?? throw new InvalidOperationException("The RPC watch request was empty.");
            var alreadyEnabled = Volatile.Read(ref _leasesEnabled) != 0;
            var revision = alreadyEnabled ? 2 : 1;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StreamContent(new CatalogWatchStream(
                    watch.AfterRevision == revision && watch.AfterSequence == revision ? [] : Frame(new RuntimeRpcAppCatalogFrame(
                        RuntimeRpcAppStreamFrameTypes.Event,
                        new RuntimeRpcCatalogEventDescriptor(revision, revision, RuntimeRpcCatalogEventKind.ResetRequired, null),
                        Error: null)),
                    LeasesEnabled.Task,
                    () => Frame(new RuntimeRpcAppCatalogFrame(
                        RuntimeRpcAppStreamFrameTypes.Event,
                        new RuntimeRpcCatalogEventDescriptor(2, 2, RuntimeRpcCatalogEventKind.Added, CreateOwnerProvider()),
                        Error: null)), alreadyEnabled))
                {
                    Headers = { ContentType = new MediaTypeHeaderValue("application/x-ndjson") },
                },
            };
        }

        private static RuntimeRpcProviderDescriptor CreateOwnerProvider()
        {
            var descriptor = CurrentPrivateRpcDescriptors.Get("younics.ai.agent.owner.runtime");
            return new RuntimeRpcProviderDescriptor(
                "younics.ai.agent",
                "2.0.0",
                "agent.owner.runtime",
                descriptor.ContractId,
                descriptor.Version,
                descriptor.Sha256,
                descriptor.AbiSha256
                    ?? throw new InvalidOperationException("The Agent owner RPC descriptor has no ABI hash."),
                ActivationId,
                ActivationEpoch: 1,
                SessionGeneration: 1,
                EndpointReference: "rpc1_agent_owner",
                CatalogRevision: 2,
                RuntimeRpcProviderState.Active,
                FaultCode: null);
        }

        private static HttpResponseMessage Json<T>(T value)
            => new(HttpStatusCode.OK) { Content = JsonContent.Create(value, options: WebJson) };

        private static byte[] Frame<T>(T frame)
            => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(frame, WebJson) + "\n");

        private sealed class CatalogWatchStream(byte[] initial, Task leasesEnabled, Func<byte[]> added, bool alreadyEnabled) : Stream
        {
            private byte[] _current = initial;
            private int _position;
            private bool _sentAdded = alreadyEnabled;

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => _position; set => throw new NotSupportedException(); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            {
                while (_position >= _current.Length)
                {
                    if (_sentAdded)
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                        return 0;
                    }

                    await leasesEnabled.WaitAsync(cancellationToken);
                    _current = added();
                    _position = 0;
                    _sentAdded = true;
                }

                var length = Math.Min(buffer.Length, _current.Length - _position);
                _current.AsMemory(_position, length).CopyTo(buffer);
                _position += length;
                return length;
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class BlockingEventStream(byte[] payload) : Stream
    {
        private int _position;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= payload.Length) return 0;
            var length = Math.Min(count, payload.Length - _position);
            payload.AsSpan(_position, length).CopyTo(buffer.AsSpan(offset, length));
            _position += length;
            return length;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (_position < payload.Length)
            {
                var length = Math.Min(buffer.Length, payload.Length - _position);
                payload.AsMemory(_position, length).CopyTo(buffer);
                _position += length;
                return ValueTask.FromResult(length);
            }

            return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
        }

        private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
