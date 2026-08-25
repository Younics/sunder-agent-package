using System.Net;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Provider.OpenAI.Auth;
using Sunder.Package.Agent.Provider.OpenAI.Transport;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
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

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await context.LogProviderEventAsync(
                PackageLogLevel.Information,
                "event",
                "message",
                cancellationToken: cancellation.Token));
        ProviderConformanceHarness.VerifyCallerCancellation("OpenAI", exception, cancellation.Token);
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

    [Fact]
    public async Task ProviderStreamTelemetry_SatisfiesCommonLifecycleAndRedactionConformance()
    {
        var handler = new CapturingHttpMessageHandler();
        var packageContext = new ProviderTestPackageContext("sunder.package.agent.provider.openai");
        using var auth = new CodexConnectedAuthStrategy(packageContext);
        using var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://chatgpt.com/backend-api/"),
        };
        var logger = new RecordingEventLogger();
        using var client = new OpenAiCodexChatClient(
            new AgentChatClientContext("openai", "openai/gpt-5.5", logger),
            "OpenAI",
            new CodexConnectedTransport(auth, httpClient),
            new CodexResponseContinuationStore(),
            new OpenAiCodexSession(
                ProviderFailureInputs.SecretCanary,
                "refresh-token",
                DateTimeOffset.UtcNow.AddHours(1),
                "account-id"));

        await foreach (var _ in client.GetStreamingResponseAsync(
                           [new ChatMessage(ChatRole.User, ProviderFailureInputs.RequestCanary)]))
        {
        }

        Assert.Equal(ProviderFailureInputs.SecretCanary, handler.AccessToken);
        Assert.Contains(ProviderFailureInputs.RequestCanary, handler.RequestBody, StringComparison.Ordinal);

        ProviderConformanceHarness.VerifyTelemetryAndRedaction(
            "OpenAI",
            logger.Entries.Select(static entry => entry.EventName),
            string.Join('\n', logger.Entries.Select(static entry => string.Join(
                "|",
                entry.EventName,
                entry.Message,
                string.Join(",", entry.Attributes.Select(static attribute => $"{attribute.Key}={attribute.Value}"))))),
            ProviderFailureInputs.SecretCanary,
            ProviderFailureInputs.RequestCanary);
    }

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        public string? AccessToken { get; private set; }

        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            AccessToken = request.Headers.Authorization?.Parameter;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""
                    data: {"type":"response.output_text.delta","delta":"ok"}

                    data: {"type":"response.completed","response":{"id":"response-id","status":"completed"}}

                    """),
            };
        }
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
        public List<(PackageLogLevel Level, string EventName, string Message, IReadOnlyDictionary<string, object?> Attributes)> Entries { get; } = [];

        public ValueTask WriteAsync(
            PackageLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
        {
            Entries.Add((level, eventName, message, attributes!));
            return ValueTask.CompletedTask;
        }
    }
}
