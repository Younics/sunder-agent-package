using System.Diagnostics;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentProviderCycleRunner(AgentStreamingTurnWriter streamingTurnWriter)
{
    private readonly AgentStreamingTurnWriter _streamingTurnWriter = streamingTurnWriter;

    public Task<AgentProviderSession> CreateSessionAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentPromptPreparation preparation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tools = preparation.RuntimeTools
            .Select(tool => (AITool)tool.Declaration)
            .ToList();
        var options = new ChatOptions
        {
            Instructions = preparation.SystemInstructions,
            ConversationId = context.Session.SessionId.ToString("N"),
            Tools = tools,
            ToolMode = tools.Count > 0 ? new AutoChatToolMode() : ChatToolMode.None,
            AllowMultipleToolCalls = preparation.AllowMultipleToolCalls,
            Reasoning = BuildReasoningOptions(context.ModelVariant),
            AdditionalProperties = BuildModelOptionProperties(context),
        };
        return Task.FromResult(new AgentProviderSession(options, context.ProviderId, context.ModelId));
    }

    public async Task<AgentProviderCycleResult> RunCycleAsync(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentProviderSession providerSession,
        IReadOnlyList<ChatMessage> promptMessages,
        AgentAssistantTurnState assistantTurnState,
        Stopwatch loopStopwatch,
        CancellationToken cancellationToken,
        AgentRunBudgetTracker? budgetTracker = null,
        long estimatedInputTokens = 1)
    {
        var streamState = _streamingTurnWriter.BeginCycle(
            host,
            context,
            assistantTurnState,
            loopStopwatch);
        var streamAttempt = 0;
        var chatClient = await providerSession.GetChatClientAsync(host, cancellationToken);
        if (host is IAgentRunActivitySink activitySink)
        {
            activitySink.ReportRunActivity(AgentRunActivityKind.Thinking, "Thinking");
        }
        var retryPipeline = AgentProviderResilience.CreatePipeline(notification => LogRetry(
            host,
            loopStopwatch,
            notification),
            () => !streamState.HasContentBearingOutput);

        try
        {
            await retryPipeline.ExecuteAsync(async attemptCancellationToken =>
            {
                budgetTracker?.ChargeProviderAttempt(Math.Max(1, estimatedInputTokens));
                if (streamAttempt > 0)
                {
                    _streamingTurnWriter.ResetForRetry(streamState);
                    host.LogEvent(
                        PackageLogLevel.Debug,
                        "behavior.loop.retry.start",
                        "Retrying provider execution from persisted transcript.",
                        loopStopwatch.ElapsedMilliseconds,
                        new Dictionary<string, object?>(StringComparer.Ordinal)
                        {
                            ["retry.attempt"] = streamAttempt,
                            ["prompt.turn_count"] = promptMessages.Count,
                        });
                }

                streamAttempt++;
                await _streamingTurnWriter.WriteAttemptAsync(
                    streamState,
                    chatClient,
                    promptMessages,
                    providerSession.Options,
                    attemptCancellationToken);
            }, cancellationToken);
        }
        catch (AgentChatProviderException ex)
            when (ex.FailureKind == AgentChatProviderFailureKind.ContextWindowExceeded)
        {
            throw new AgentProviderContextWindowException(
                ex,
                streamState.HasContentBearingOutput,
                Math.Max(1, estimatedInputTokens));
        }
        finally
        {
            providerSession.ReleaseChatClient(chatClient);
        }

        return _streamingTurnWriter.CompleteCycle(streamState);
    }

    public bool IsTransientFailure(Exception exception, CancellationToken cancellationToken)
        => AgentProviderResilience.IsTransient(exception, cancellationToken);

    private static void LogRetry(
        IAgentBehaviorLoopRuntime host,
        Stopwatch loopStopwatch,
        AgentProviderRetryNotification notification)
        => host.LogEvent(
            PackageLogLevel.Warning,
            "provider.stream.retrying",
            $"Transient provider stream interruption. Retrying in {notification.Delay.TotalSeconds:0.#}s (attempt {notification.AttemptNumber}/{notification.MaxRetryAttempts}).",
            loopStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["retry.attempt"] = notification.AttemptNumber,
                ["retry.max_attempts"] = notification.MaxRetryAttempts,
                ["retry.delay_ms"] = notification.Delay.TotalMilliseconds,
                ["retry.exception_type"] = notification.Exception.GetType().FullName,
            },
            notification.Exception);

    private static ReasoningOptions? BuildReasoningOptions(AgentModelVariantDescriptor? variant)
        => variant?.ReasoningEffort is null
            ? null
            : new ReasoningOptions
            {
                Effort = ToReasoningEffort(variant.ReasoningEffort.Value),
                Output = variant.ReasoningEffort.Value == AgentReasoningEffort.None
                    ? ReasoningOutput.None
                    : ReasoningOutput.Summary,
            };

    private static AdditionalPropertiesDictionary? BuildModelOptionProperties(AgentBehaviorLoopContext context)
    {
        if (context.ModelSpeedOption is null && context.ModelModeOption is null)
        {
            return null;
        }

        var properties = new AdditionalPropertiesDictionary();
        if (!string.IsNullOrWhiteSpace(context.ModelSpeedOption?.SpeedOptionId))
        {
            properties[AgentChatModelOptionKeys.SpeedOptionId] = context.ModelSpeedOption.SpeedOptionId;
        }

        if (!string.IsNullOrWhiteSpace(context.ModelModeOption?.ModeOptionId))
        {
            properties[AgentChatModelOptionKeys.ModeOptionId] = context.ModelModeOption.ModeOptionId;
        }

        return properties;
    }

    private static ReasoningEffort ToReasoningEffort(AgentReasoningEffort effort)
        => effort switch
        {
            AgentReasoningEffort.None => ReasoningEffort.None,
            AgentReasoningEffort.Low => ReasoningEffort.Low,
            AgentReasoningEffort.Medium => ReasoningEffort.Medium,
            AgentReasoningEffort.High => ReasoningEffort.High,
            AgentReasoningEffort.ExtraHigh => ReasoningEffort.ExtraHigh,
            _ => ReasoningEffort.Medium,
        };
}

internal sealed class AgentProviderContextWindowException(
    AgentChatProviderException providerException,
    bool hasContentBearingOutput,
    long estimatedInputTokens) : Exception(providerException.Message, providerException)
{
    public AgentChatProviderException ProviderException { get; } = providerException;

    public bool HasContentBearingOutput { get; } = hasContentBearingOutput;

    public long EstimatedInputTokens { get; } = estimatedInputTokens;
}

internal sealed class AgentProviderSession(
    ChatOptions options,
    string providerId,
    string modelId)
{
    private IChatClient? _chatClient;

    public ChatOptions Options { get; } = options;

    public async ValueTask<IChatClient> GetChatClientAsync(
        IAgentBehaviorLoopRuntime host,
        CancellationToken cancellationToken)
    {
        _chatClient ??= await host.CreateChatClientAsync(
            new AgentChatClientContext(providerId, modelId),
            cancellationToken);
        return _chatClient;
    }

    public void ReleaseChatClient(IChatClient chatClient)
    {
        if (ReferenceEquals(_chatClient, chatClient))
        {
            _chatClient = null;
        }

        chatClient.Dispose();
    }
}
