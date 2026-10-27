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
        AgentRunUsageTracker? usageTracker = null,
        long estimatedInputTokens = 1,
        AgentProviderRetryState? retryState = null)
    {
        var streamState = _streamingTurnWriter.BeginCycle(
            host,
            context,
            assistantTurnState,
            loopStopwatch);
        retryState ??= new AgentProviderRetryState();
        var chatClient = await providerSession.GetChatClientAsync(host, cancellationToken);
        if (host is IAgentRunActivitySink activitySink)
        {
            activitySink.ReportRunActivity(AgentRunActivityKind.Thinking, "Thinking");
        }
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (usageTracker is not null)
                        await usageTracker.ChargeProviderAttemptAsync(Math.Max(1, estimatedInputTokens), cancellationToken).ConfigureAwait(false);
                    await _streamingTurnWriter.WriteAttemptAsync(streamState, chatClient, promptMessages, providerSession.Options, cancellationToken);
                    break;
                }
                catch (Exception error) when (AgentProviderResilience.IsTransient(error, cancellationToken))
                {
                    var retry = retryState.TrySchedule(error, streamState.HasContentBearingOutput, cancellationToken);
                    if (retry is null)
                        throw new AgentProviderRecoveryExhaustedException(error, retryState.RetryCount + 1);
                    streamState.ReasoningActivity.Flush();
                    await WaitForRetryAsync(host, loopStopwatch, retryState, retry, error, cancellationToken);
                    if (streamState.HasContentBearingOutput)
                    {
                        // Only successful complete responses can dispatch local tools.
                        // Retain visible text, discard this incomplete batch and rebuild
                        // from durable transcript instead of replaying the old prompt.
                        AgentLoopTerminalHandler.PreservePartialTurn(host, assistantTurnState);
                        throw new AgentProviderContinuationException(error);
                    }
                    _streamingTurnWriter.ResetForRetry(streamState);
                    (host as IAgentRunActivitySink)?.ReportRunActivity(AgentRunActivityKind.Thinking, "Thinking");
                }
            }
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

    private static async Task WaitForRetryAsync(
        IAgentBehaviorLoopRuntime host,
        Stopwatch loopStopwatch,
        AgentProviderRetryState state,
        AgentRunRetryState retry,
        Exception error,
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!host.IsCurrentRun()) throw new AgentRunTranscriptWriteRejectedException();
        if (host is IAgentProviderRetrySink retrySink) retrySink.ReportProviderRetry(retry);
        else (host as IAgentRunActivitySink)?.ReportRunActivity(AgentRunActivityKind.Retrying, retry.Reason);
        var remaining = retry.RetryAtUtc - state.Clock.GetUtcNow();
        host.LogEvent(
            PackageLogLevel.Warning,
            "provider.stream.retrying",
            $"{retry.Reason}. {(retry.IsContinuation ? "Continuing" : "Retrying")} in {Math.Max(0, remaining.TotalSeconds):0.#}s (attempt {retry.Attempt}/11).",
            loopStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["retry.attempt"] = retry.Attempt,
                ["retry.max_attempts"] = 11,
                ["retry.at_utc"] = retry.RetryAtUtc,
                ["retry.continuation"] = retry.IsContinuation,
                ["retry.exception_type"] = error.GetType().FullName,
            },
            error);
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining, state.Clock, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        if (!host.IsCurrentRun()) throw new AgentRunTranscriptWriteRejectedException();
    }

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
        var properties = new AdditionalPropertiesDictionary
        {
            [AgentChatModelOptionKeys.SpeedOptionId] = context.ModelSpeedOption?.SpeedOptionId ?? "normal",
        };
        if (!string.IsNullOrWhiteSpace(context.ModelVariant?.VariantId))
        {
            properties[AgentChatModelOptionKeys.ReasoningVariantId] = context.ModelVariant.VariantId;
        }
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
            _ => throw new InvalidOperationException($"Unsupported reasoning hint '{effort}'."),
        };
}

internal sealed class AgentProviderContinuationException(Exception error) : Exception("Continue from retained partial output.", error);

internal sealed class AgentProviderRecoveryExhaustedException(Exception error, int attempts)
    : Exception($"The provider could not recover after {attempts} attempts. Saved progress is retained. Choose Continue when the provider is available.", error);

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
