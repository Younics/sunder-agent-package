using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Callbacks;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class OpenAiSettingsViewModelTests
{
    [Fact]
    public async Task AuthMode_DefaultsToCodexConnectedAndPersistsSelection()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        var viewModel = new OpenAiSettingsViewModel(
            context,
            new StubRuntimeClient(new OpenAiAuthOperationResult(false, false, null)));

        Assert.Equal(OpenAiAuthMode.CodexConnected, viewModel.SelectedAuthMode?.ModeId);

        viewModel.SelectedAuthMode = viewModel.AuthModes.Single(mode => mode.ModeId == OpenAiAuthMode.ApiKey);
        await viewModel.SaveCommand.ExecuteAsync(null);

        Assert.Equal(OpenAiAuthMode.ApiKey, await context.Settings.GetStoredValueAsync(OpenAiAuthMode.ConfigurationKey));
    }

    [Fact]
    public async Task RuntimeExpiredSession_IsNotReportedAsConnected()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        using var viewModel = new OpenAiSettingsViewModel(
            context,
            new StubRuntimeClient(new OpenAiAuthOperationResult(
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
        var runtime = new StubRuntimeClient(new OpenAiAuthOperationResult(
            true,
            true,
            DateTimeOffset.UtcNow.AddHours(1)));
        using var viewModel = CreateViewModel(context, callbacks, runtime);

        await viewModel.AuthorizeCommand.ExecuteAsync(null);

        Assert.Equal(OpenAiAuthMode.CodexConnected, modeAtStart);
        Assert.Equal(PackageCallbackHandlerIds.Authentication, callbacks.StartedHandlerId);
        Assert.Equal(1, callbacks.OpenCount);
        Assert.True(viewModel.IsCodexConnected);
        Assert.Equal("Connected, active", viewModel.CodexStatusLabel);
        Assert.True(runtime.InvocationCount > 0);
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
            new StubRuntimeClient(new OpenAiAuthOperationResult(true, true, DateTimeOffset.UtcNow.AddHours(1))));

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
            new StubRuntimeClient(new OpenAiAuthOperationResult(true, true, DateTimeOffset.UtcNow.AddHours(1))));

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

    private static ProviderTestPackageContext Context(FakePackageCallbackClient callbacks)
        => new("sunder.package.agent.provider.openai", callbacks: callbacks);

    private static OpenAiSettingsViewModel CreateViewModel(
        ProviderTestPackageContext context,
        FakePackageCallbackClient callbacks,
        StubRuntimeClient? runtime = null,
        PackageCallbackFlowRunner? callbackFlow = null)
    {
        runtime ??= new StubRuntimeClient(new OpenAiAuthOperationResult(false, false, null));
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
            new OpenAiAuthPresentationService(runtime),
            callbackFlow,
            new ProviderCredentialAccessor(context.Secrets, OpenAiProviderConfiguration.ApiKeySecretKey));
    }

    private static PackageCallbackSessionStatus CallbackStatus(
        PackageCallbackSessionState state,
        string message = "Continue in browser.")
        => new(
            "sunder.package.agent.provider.openai",
            PackageCallbackHandlerIds.Authentication,
            "openai-session",
            state,
            message,
            new Uri("https://auth.openai.example/authorize"),
            DateTimeOffset.UtcNow.AddMinutes(5));

    private sealed class StubRuntimeClient(OpenAiAuthOperationResult response) : IPackageRuntimeClient
    {
        public bool IsAvailable => true;

        public int InvocationCount { get; private set; }

        public ValueTask<TResponse> InvokeAsync<TRequest, TResponse>(
            PackageRuntimeOperation<TRequest, TResponse> operation,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TResponse : class
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.Equal(OpenAiRuntimeOperations.Auth.OperationId, operation.OperationId);
            InvocationCount++;
            return ValueTask.FromResult((TResponse)(object)response);
        }

        public IAsyncEnumerable<TEvent> SubscribeAsync<TRequest, TEvent>(
            PackageRuntimeStream<TRequest, TEvent> stream,
            TRequest request,
            CancellationToken cancellationToken = default)
            where TRequest : class
            where TEvent : class
            => throw new NotSupportedException();
    }
}
