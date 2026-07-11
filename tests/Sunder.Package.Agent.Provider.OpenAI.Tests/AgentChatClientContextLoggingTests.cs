using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Provider.OpenAI.Tests;

public sealed class AgentChatClientContextLoggingTests
{
    [Fact]
    public async Task LogProviderEventAsync_SwallowsAsynchronousSinkFailure()
    {
        var context = new AgentChatClientContext("provider", "model", new ThrowingEventSink(new InvalidOperationException("sink failed")));

        await context.LogProviderEventAsync(AgentLogLevel.Information, "event", "message");
    }

    [Fact]
    public async Task LogProviderEventAsync_PropagatesAsynchronousCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var context = new AgentChatClientContext("provider", "model", new ThrowingEventSink(new OperationCanceledException(cancellation.Token)));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await context.LogProviderEventAsync(
                AgentLogLevel.Information,
                "event",
                "message",
                cancellationToken: cancellation.Token));
    }

    private sealed class ThrowingEventSink(Exception exception) : IAgentProviderEventSink
    {
        public async ValueTask WriteAsync(
            AgentLogLevel level,
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
}
