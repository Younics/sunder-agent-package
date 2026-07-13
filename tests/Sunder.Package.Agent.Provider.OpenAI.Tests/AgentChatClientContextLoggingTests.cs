using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class AgentChatClientContextLoggingTests
{
    [Fact]
    public async Task LogProviderEventAsync_SwallowsAsynchronousLoggerFailure()
    {
        var context = new AgentChatClientContext("provider", "model", new ThrowingEventLogger(new InvalidOperationException("logger failed")));

        await context.LogProviderEventAsync(PackageLogLevel.Information, "event", "message");
    }

    [Fact]
    public async Task LogProviderEventAsync_PropagatesAsynchronousCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new AgentChatClientContext("provider", "model", new ThrowingEventLogger(new OperationCanceledException(cancellation.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await context.LogProviderEventAsync(
                PackageLogLevel.Information,
                "event",
                "message",
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public async Task LogProviderEventAsync_MergesAgentCorrelationAttributes()
    {
        var logger = new RecordingEventLogger();
        var context = new AgentChatClientContext(
            "provider",
            "model",
            logger,
            new Dictionary<string, object?>
            {
                ["run.id"] = "run-1",
            });

        await context.LogProviderEventAsync(
            PackageLogLevel.Debug,
            "provider.request.start",
            "started",
            elapsedMilliseconds: 12,
            attributes: new Dictionary<string, object?>
            {
                ["request.id"] = "request-1",
            });

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(PackageLogLevel.Debug, entry.Level);
        Assert.Equal("provider.request.start", entry.EventName);
        Assert.Equal("provider.request.start", entry.Attributes["event.name"]);
        Assert.Equal("provider", entry.Attributes["provider.id"]);
        Assert.Equal("model", entry.Attributes["model.id"]);
        Assert.Equal("run-1", entry.Attributes["run.id"]);
        Assert.Equal(12L, entry.Attributes["duration.ms"]);
        Assert.Equal("request-1", entry.Attributes["request.id"]);
    }

    private sealed class ThrowingEventLogger(Exception exception) : IPackageEventLogger
    {
        public async ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exceptionValue = null,
            CancellationToken cancellationToken = default)
        {
            await Task.Yield();
            throw exception;
        }
    }

    private sealed class RecordingEventLogger : IPackageEventLogger
    {
        public List<(PackageLogLevel Level, string EventName, IReadOnlyDictionary<string, object?> Attributes)> Entries { get; } = [];

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((level, eventName, attributes!));
            return ValueTask.CompletedTask;
        }
    }
}
