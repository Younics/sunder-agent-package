extern alias OpenAiApp;

using OpenAiSettingsViewModel = OpenAiApp::Sunder.Package.Agent.Provider.OpenAI.OpenAiSettingsViewModel;
using Sunder.Package.Agent.Provider.TestSupport;
using OpenAiApp::Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Callbacks;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

using OpenAiAuthOperationResult = OpenAiApp::Sunder.Package.Agent.Provider.OpenAI.OpenAiAuthOperationResult;
using IManagementService = OpenAiApp::Sunder.Generated.Packages.Younics_Ai_Agent_Provider_Openai_Services.IManagementService;

public sealed class OpenAiSettingsViewModelTests
{
    [Fact]
    public async Task AuthMode_DefaultsToCodexConnectedAndPersistsSelection()
    {
        var context = new ProviderTestPackageContext("younics.ai.agent.provider.openai");
        var viewModel = new OpenAiSettingsViewModel(
            context,
            new StubOwnerManagementClient(new OpenAiAuthOperationResult(false, false, null)));

        Assert.Equal(OpenAiAuthMode.CodexConnected, viewModel.SelectedAuthMode?.ModeId);

        viewModel.SelectedAuthMode = viewModel.AuthModes.Single(mode => mode.ModeId == OpenAiAuthMode.ApiKey);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(OpenAiAuthMode.ApiKey, await context.Settings.GetStoredValueAsync(OpenAiAuthMode.ConfigurationKey));
    }

    [Fact]
    public async Task RuntimeExpiredSession_IsNotReportedAsConnected()
    {
        var context = new ProviderTestPackageContext("younics.ai.agent.provider.openai");
        using var viewModel = new OpenAiSettingsViewModel(
            context,
            new StubOwnerManagementClient(new OpenAiAuthOperationResult(
                false,
                true,
                null,
                "temporarily unavailable")));

        await viewModel.RefreshStatusCommand.ExecuteAsync(null);

        Assert.False(viewModel.IsCodexConnected);
        Assert.True(viewModel.IsCodexStatusError);
        Assert.True(viewModel.CanDisconnect);
        Assert.Equal("Session expired", viewModel.CodexStatusLabel);
    }

    [Fact]
    public void Authorize_IsGatedByModeCallbackAvailabilityAndActiveOperation()
    {
        var unavailableCallbacks = new FakePackageCallbackClient { IsAvailable = false };
        var unavailableContext = Context(unavailableCallbacks);
        using var unavailable = CreateViewModel(unavailableContext, unavailableCallbacks);

        Assert.False(unavailable.CanAuthorize);
        Assert.False(unavailable.AuthorizeCommand.CanExecute(null));

        var callbacks = new FakePackageCallbackClient();
        var context = Context(callbacks);
        using var apiKeyMode = CreateViewModel(context, callbacks);
        apiKeyMode.SelectedAuthMode = apiKeyMode.AuthModes.Single(mode => mode.ModeId == OpenAiAuthMode.ApiKey);

        Assert.False(apiKeyMode.CanAuthorize);
        Assert.False(apiKeyMode.AuthorizeCommand.CanExecute(null));
    }

    [Fact]
    public async Task Authorize_PersistsCodexModeBeforeStartingCallbackAndRefreshesRuntimeStatus()
    {
        var callbacks = new FakePackageCallbackClient();
        var context = Context(callbacks);
        string? modeAtStart = null;
        callbacks.StartOperation = async cancellationToken =>
        {
            modeAtStart = await context.Settings.GetStoredValueAsync(
                OpenAiAuthMode.ConfigurationKey,
                cancellationToken);
            return CallbackStatus(PackageCallbackSessionState.Pending);
        };
        callbacks.Enqueue(CallbackStatus(PackageCallbackSessionState.Completed));
        var management = new StubOwnerManagementClient(new OpenAiAuthOperationResult(
            true,
            true,
            DateTimeOffset.UtcNow.AddHours(1)));
        using var viewModel = CreateViewModel(context, callbacks, management);

        await viewModel.AuthorizeCommand.ExecuteAsync(null);

        Assert.Equal(OpenAiAuthMode.CodexConnected, modeAtStart);
        Assert.Equal(PackageCallbackHandlerIds.Authentication, callbacks.StartedHandlerId);
        Assert.Equal(1, callbacks.OpenCount);
        Assert.True(viewModel.IsCodexConnected);
        Assert.Equal("Connected, active", viewModel.CodexStatusLabel);
        Assert.True(management.InvocationCount > 0);
    }

    [Fact]
    public async Task Authorize_SuppressesDuplicateStartsWhileOperationIsActive()
    {
        var callbacks = new FakePackageCallbackClient();
        var context = Context(callbacks);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        callbacks.StartOperation = async cancellationToken =>
        {
            started.TrySetResult();
            await release.Task.WaitAsync(cancellationToken);
            return CallbackStatus(PackageCallbackSessionState.Completed);
        };
        using var viewModel = CreateViewModel(
            context,
            callbacks,
            new StubOwnerManagementClient(new OpenAiAuthOperationResult(true, true, DateTimeOffset.UtcNow.AddHours(1))));

        var first = viewModel.AuthorizeCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var duplicate = viewModel.AuthorizeCommand.ExecuteAsync(null);

        Assert.False(viewModel.CanAuthorize);
        Assert.Equal(1, callbacks.StartCount);
        release.TrySetResult();
        await Task.WhenAll(first, duplicate);
        Assert.Equal(1, callbacks.StartCount);
    }

    [Fact]
    public async Task Authorize_AllowsReauthorizationAfterCompletion()
    {
        var callbacks = new FakePackageCallbackClient
        {
            StartOperation = _ => ValueTask.FromResult(CallbackStatus(PackageCallbackSessionState.Pending)),
        };
        callbacks.Enqueue(
            CallbackStatus(PackageCallbackSessionState.Completed),
            CallbackStatus(PackageCallbackSessionState.Completed));
        var context = Context(callbacks);
        using var viewModel = CreateViewModel(
            context,
            callbacks,
            new StubOwnerManagementClient(new OpenAiAuthOperationResult(true, true, DateTimeOffset.UtcNow.AddHours(1))));

        await viewModel.AuthorizeCommand.ExecuteAsync(null);
        Assert.True(viewModel.AuthorizeCommand.CanExecute(null));
        Assert.Equal("Reauthorize with ChatGPT Plus/Pro", viewModel.AuthorizationButtonLabel);
        await viewModel.AuthorizeCommand.ExecuteAsync(null);

        Assert.Equal(2, callbacks.StartCount);
        Assert.Equal(2, callbacks.OpenCount);
    }

    [Fact]
    public async Task Authorize_PresentsActionableCallbackFailure()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(
            CallbackStatus(PackageCallbackSessionState.Pending),
            CallbackStatus(PackageCallbackSessionState.Failed, "OpenAI denied the request."));
        var context = Context(callbacks);
        using var viewModel = CreateViewModel(context, callbacks);

        await viewModel.AuthorizeCommand.ExecuteAsync(null);

        Assert.True(viewModel.IsCodexStatusError);
        Assert.Equal("Authorization failed", viewModel.CodexStatusLabel);
        Assert.Contains("OpenAI denied", viewModel.CodexStatusDetail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Dispose_CancelsOwnedAuthorizationPolling()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(CallbackStatus(PackageCallbackSessionState.Pending));
        var context = Context(callbacks);
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new PackageCallbackFlowRunner(
            callbacks,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(50),
            async (_, cancellationToken) =>
            {
                delayStarted.TrySetResult();
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    cancellationObserved.TrySetResult();
                    throw;
                }
            });
        var viewModel = CreateViewModel(context, callbacks, callbackFlow: runner);

        var authorization = viewModel.AuthorizeCommand.ExecuteAsync(null);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        viewModel.Dispose();
        await authorization;

        await cancellationObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));
        Assert.Equal(1, callbacks.StartCount);
        Assert.Equal(1, callbacks.OpenCount);
    }

    [Fact]
    public async Task ExpectedFailure_ReconciliationPublishesBoundStateThroughDispatcher()
    {
        var callbacks = new FakePackageCallbackClient();
        var context = Context(callbacks);
        var management = new FailOnceOwnerManagementClient();
        var dispatcher = new TrackingPresentationDispatcher();
        using var viewModel = new OpenAiSettingsViewModel(
            context,
            management,
            new PackageCallbackFlowRunner(callbacks),
            PresentationOperationObserver.CreateUnreported("OpenAI settings test"),
            dispatcher);
        var offDispatcherNotifications = 0;
        viewModel.PropertyChanged += (_, _) =>
        {
            if (!dispatcher.IsDispatching)
            {
                Interlocked.Increment(ref offDispatcherNotifications);
            }
        };

        await viewModel.RefreshStatusCommand.ExecuteAsync(null);

        Assert.True(management.AuthStatusCallCount >= 2);
        Assert.Equal(0, Volatile.Read(ref offDispatcherNotifications));
        Assert.Equal("Not connected", viewModel.CodexStatusLabel);
    }

    private static ProviderTestPackageContext Context(FakePackageCallbackClient callbacks)
        => new("younics.ai.agent.provider.openai", callbacks: callbacks);

    private static OpenAiSettingsViewModel CreateViewModel(
        ProviderTestPackageContext context,
        FakePackageCallbackClient callbacks,
        StubOwnerManagementClient? management = null,
        PackageCallbackFlowRunner? callbackFlow = null)
    {
        management ??= new StubOwnerManagementClient(new OpenAiAuthOperationResult(false, false, null));
        callbackFlow ??= new PackageCallbackFlowRunner(
            callbacks,
            TimeProvider.System,
            TimeSpan.FromMilliseconds(10),
            (_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            });
        return new OpenAiSettingsViewModel(
            context,
            management,
            callbackFlow,
            PresentationOperationObserver.CreateUnreported("OpenAI settings test"));
    }

    private static PackageCallbackSessionStatus CallbackStatus(
        PackageCallbackSessionState state,
        string message = "Continue in browser.")
        => new(
            "younics.ai.agent.provider.openai",
            PackageCallbackHandlerIds.Authentication,
            "openai-session",
            state,
            message,
            new Uri("https://auth.openai.example/authorize"),
            DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class StubOwnerManagementClient(OpenAiAuthOperationResult response) : IManagementService
    {
        private bool _hasCredential;

        public int InvocationCount { get; private set; }

        public ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(response);
        }

        public ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(new OpenAiAuthOperationResult(false, false, null));
        }

        public ValueTask<bool> GetCredentialStatusAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            return ValueTask.FromResult(_hasCredential);
        }

        public ValueTask<bool> SetCredentialAsync(
            string credential,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            _hasCredential = true;
            return ValueTask.FromResult(true);
        }

        public ValueTask<bool> ClearCredentialAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InvocationCount++;
            _hasCredential = false;
            return ValueTask.FromResult(false);
        }
    }

    private sealed class FailOnceOwnerManagementClient : IManagementService
    {
        private int _authStatusCallCount;

        public int AuthStatusCallCount => Volatile.Read(ref _authStatusCallCount);

        public async ValueTask<OpenAiAuthOperationResult> GetAuthStatusAsync(
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            cancellationToken.ThrowIfCancellationRequested();
            if (Interlocked.Increment(ref _authStatusCallCount) == 1)
            {
                throw new IOException("Injected asynchronous auth status failure.");
            }
            return new OpenAiAuthOperationResult(false, false, null);
        }

        public ValueTask<OpenAiAuthOperationResult> DisconnectAuthAsync(
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(new OpenAiAuthOperationResult(false, false, null));

        public ValueTask<bool> GetCredentialStatusAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);

        public ValueTask<bool> SetCredentialAsync(
            string credential,
            CancellationToken cancellationToken = default)
            => ValueTask.FromResult(true);

        public ValueTask<bool> ClearCredentialAsync(CancellationToken cancellationToken = default)
            => ValueTask.FromResult(false);
    }

    private sealed class TrackingPresentationDispatcher : IPresentationDispatcher
    {
        private readonly AsyncLocal<int> _dispatchDepth = new();

        public bool IsDispatching => _dispatchDepth.Value > 0;

        public bool CheckAccess() => IsDispatching;

        public Task InvokeAsync(Action action)
        {
            _dispatchDepth.Value++;
            try
            {
                action();
                return Task.CompletedTask;
            }
            finally
            {
                _dispatchDepth.Value--;
            }
        }
    }
}
