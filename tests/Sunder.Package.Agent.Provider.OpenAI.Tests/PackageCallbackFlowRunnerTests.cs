extern alias OpenAiApp;

using OpenAiApp::Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Callbacks;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class PackageCallbackFlowRunnerTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 13, 12, 0, 0, TimeSpan.Zero);
    private static readonly Uri LaunchUri = new("https://auth.example/authorize");

    [Fact]
    public async Task RunAsync_RejectsUnavailableCallbacksBeforeStart()
    {
        var callbacks = new FakePackageCallbackClient { IsAvailable = false };
        var runner = CreateRunner(callbacks);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Contains("unavailable", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, callbacks.StartCount);
    }

    [Fact]
    public async Task RunAsync_OpensBrowserOnceAndPollsPendingToCompleted()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(
            Status(PackageCallbackSessionState.Pending),
            Status(PackageCallbackSessionState.Pending),
            Status(PackageCallbackSessionState.Completed, "Connected."));
        var delays = 0;
        var runner = CreateRunner(callbacks, (_, cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            delays++;
            return Task.CompletedTask;
        });

        var result = await runner.RunAsync("auth", null, "Test authorization");

        Assert.Equal(PackageCallbackSessionState.Completed, result.State);
        Assert.Equal(1, callbacks.StartCount);
        Assert.Equal(1, callbacks.OpenCount);
        Assert.Equal(2, callbacks.StatusCount);
        Assert.Equal(2, delays);
        Assert.Equal(LaunchUri, callbacks.OpenedUri);
    }

    [Fact]
    public async Task RunAsync_RejectsMissingLaunchUriWithoutOpeningBrowser()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(Status(PackageCallbackSessionState.Pending) with { LaunchUri = null });
        var runner = CreateRunner(callbacks);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Contains("launch URI", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, callbacks.OpenCount);
        Assert.Equal(1, callbacks.CancelCount);
    }

    [Fact]
    public async Task RunAsync_PreservesImmediateFailureWithoutRequiringLaunchUri()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(Status(PackageCallbackSessionState.Failed, "Provider rejected the request.") with
        {
            LaunchUri = null,
        });
        var runner = CreateRunner(callbacks);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Contains("Provider rejected", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, callbacks.OpenCount);
    }

    [Fact]
    public async Task RunAsync_ReturnsImmediateCompletionWithoutOpeningBrowser()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(Status(PackageCallbackSessionState.Completed, "Already connected.") with
        {
            LaunchUri = null,
        });
        var runner = CreateRunner(callbacks);

        var result = await runner.RunAsync("auth", null, "Test authorization");

        Assert.Equal(PackageCallbackSessionState.Completed, result.State);
        Assert.Equal(0, callbacks.OpenCount);
    }

    [Theory]
    [InlineData(PackageCallbackSessionState.Failed, "failed")]
    [InlineData(PackageCallbackSessionState.Cancelled, "cancelled")]
    public async Task RunAsync_ReportsNonSuccessfulTerminalStatus(
        PackageCallbackSessionState terminalState,
        string expectedMessage)
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(
            Status(PackageCallbackSessionState.Pending),
            Status(terminalState, "Provider detail."));
        var runner = CreateRunner(callbacks);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Contains(expectedMessage, error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Provider detail", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, callbacks.OpenCount);
    }

    [Fact]
    public async Task RunAsync_HonorsExpiredStartWithoutOpeningBrowser()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(Status(PackageCallbackSessionState.Pending) with { ExpiresAtUtc = Now });
        var runner = CreateRunner(callbacks);

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Contains("expired", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, callbacks.OpenCount);
        Assert.Equal(0, callbacks.StatusCount);
        Assert.Equal(1, callbacks.CancelCount);
        Assert.Equal("session-1", callbacks.CancelledSessionId);
    }

    [Fact]
    public async Task RunAsync_BrowserOpenFailure_CancelsSessionAndPreservesOriginalFailure()
    {
        var openFailure = new InvalidOperationException("The browser could not be opened.");
        var callbacks = new FakePackageCallbackClient
        {
            OpenOperation = _ => ValueTask.FromException(openFailure),
            CancelOperation = _ => ValueTask.FromException<bool>(new InvalidOperationException("Cleanup failed.")),
        };
        callbacks.Enqueue(Status(PackageCallbackSessionState.Pending));
        var runner = CreateRunner(callbacks);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Same(openFailure, error);
        Assert.Equal(1, callbacks.CancelCount);
        Assert.Equal("session-1", callbacks.CancelledSessionId);
    }

    [Fact]
    public async Task RunAsync_StalledCleanupIsBoundedAndPreservesOriginalFailure()
    {
        var openFailure = new InvalidOperationException("The browser could not be opened.");
        var stalledCleanup = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbacks = new FakePackageCallbackClient
        {
            OpenOperation = _ => ValueTask.FromException(openFailure),
            CancelOperation = _ => new ValueTask<bool>(stalledCleanup.Task),
        };
        callbacks.Enqueue(Status(PackageCallbackSessionState.Pending));
        var runner = CreateRunner(callbacks, cleanupTimeout: TimeSpan.FromMilliseconds(20));

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            runner.RunAsync("auth", null, "Test authorization").WaitAsync(TimeSpan.FromSeconds(5)));

        Assert.Same(openFailure, error);
        Assert.Equal(1, callbacks.CancelCount);
        Assert.Equal("session-1", callbacks.CancelledSessionId);
    }

    [Fact]
    public async Task RunAsync_CallerCancellation_CancelsPendingSession()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(Status(PackageCallbackSessionState.Pending));
        var delayStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = CreateRunner(callbacks, async (_, cancellationToken) =>
        {
            delayStarted.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
        });
        using var cancellation = new CancellationTokenSource();

        var run = runner.RunAsync("auth", null, "Test authorization", cancellation.Token);
        await delayStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
        Assert.Equal(1, callbacks.CancelCount);
        Assert.Equal("session-1", callbacks.CancelledSessionId);
    }

    [Fact]
    public async Task RunAsync_LocalExpiryWhilePolling_CancelsPendingSession()
    {
        var callbacks = new FakePackageCallbackClient();
        var expiry = Now.AddMilliseconds(50);
        callbacks.Enqueue(
            Status(PackageCallbackSessionState.Pending) with { ExpiresAtUtc = expiry },
            Status(PackageCallbackSessionState.Pending) with { ExpiresAtUtc = expiry });
        var timeProvider = new MutableTimeProvider(Now);
        var runner = CreateRunner(
            callbacks,
            (duration, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                timeProvider.Advance(duration);
                return Task.CompletedTask;
            },
            timeProvider);

        await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Equal(1, callbacks.CancelCount);
        Assert.Equal("session-1", callbacks.CancelledSessionId);
    }

    [Fact]
    public async Task RunAsync_ReportsHostExpiredTerminalStatus()
    {
        var callbacks = new FakePackageCallbackClient();
        callbacks.Enqueue(
            Status(PackageCallbackSessionState.Pending),
            Status(PackageCallbackSessionState.Expired, "Host session expired."));
        var runner = CreateRunner(callbacks);

        var error = await Assert.ThrowsAsync<TimeoutException>(() =>
            runner.RunAsync("auth", null, "Test authorization"));

        Assert.Contains("Host session expired", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, callbacks.CancelCount);
    }

    private static PackageCallbackFlowRunner CreateRunner(
        FakePackageCallbackClient callbacks,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeProvider? timeProvider = null,
        TimeSpan? cleanupTimeout = null)
        => new(
            callbacks,
            timeProvider ?? new FixedTimeProvider(Now),
            TimeSpan.FromMilliseconds(50),
            delay ?? ((_, cancellationToken) =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.CompletedTask;
            }),
            cleanupTimeout);

    private static PackageCallbackSessionStatus Status(
        PackageCallbackSessionState state,
        string message = "Continue in browser.")
        => new(
            "test.package",
            "auth",
            "session-1",
            state,
            message,
            LaunchUri,
            Now.AddMinutes(5));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class MutableTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan duration) => _now = _now.Add(duration);
    }
}
