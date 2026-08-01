using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.AI;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentStreamingTurnWriter(
    AgentLoopTerminalHandler terminalHandler,
    TimeProvider? timeProvider = null)
{
    private static readonly TimeSpan AssistantStreamFlushInterval = TimeSpan.FromMilliseconds(50);
    private readonly AgentLoopTerminalHandler _terminalHandler = terminalHandler;
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public AgentStreamingTurnState BeginCycle(
        IAgentBehaviorLoopRuntime host,
        AgentBehaviorLoopContext context,
        AgentAssistantTurnState assistantTurnState,
        Stopwatch loopStopwatch)
        => new(host, context, assistantTurnState, loopStopwatch, _timeProvider);

    public void ResetForRetry(AgentStreamingTurnState state)
    {
        lock (state.SyncRoot)
        {
            if (state.AssistantTurnState.Turn is not null && state.Content.Length > 0)
            {
                state.AssistantTurnState.Turn = state.Host.UpsertAssistantTurn(
                    state.AssistantTurnState.Turn,
                    string.Empty);
            }

            state.Content.Clear();
            state.PersistedContentLength = 0;
            state.ToolCalls.Clear();
            state.ProviderUsage = null;
            state.LastAssistantFlushTimestamp = null;
            state.SuppressPendingFlush = false;
        }
    }

    public async Task WriteAttemptAsync(
        AgentStreamingTurnState state,
        IChatClient chatClient,
        IReadOnlyList<ChatMessage> promptMessages,
        ChatOptions chatOptions,
        CancellationToken cancellationToken)
    {
        using var flushCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var flushTask = FlushPendingTextAsync(state, flushCancellation.Token);
        var protocolLeakDetected = false;
        try
        {
            await foreach (var streamUpdate in chatClient.GetStreamingResponseAsync(
                               promptMessages,
                               chatOptions,
                               cancellationToken))
            {
                if (!state.Host.IsCurrentRun())
                {
                    state.TerminalResult = new AgentBehaviorLoopResult(
                        state.Context.RunningCheckpoint,
                        AgentBehaviorLoopCompletionKind.Interrupted);
                    return;
                }

                if (!string.IsNullOrEmpty(streamUpdate.Text)
                    || streamUpdate.Contents.Any(IsContentBearingOutput))
                {
                    state.HasContentBearingOutput = true;
                }

                foreach (var functionCall in streamUpdate.Contents.OfType<FunctionCallContent>())
                {
                    state.ToolCalls.Add(functionCall);
                }

                foreach (var usage in streamUpdate.Contents.OfType<UsageContent>())
                {
                    state.ProviderUsage = AgentProviderUsage.Merge(state.ProviderUsage, usage.Details);
                }

                foreach (var reasoningContent in streamUpdate.Contents.OfType<TextReasoningContent>())
                {
                    state.ReasoningActivity.Append(reasoningContent.Text, state.LoopStopwatch.Elapsed);
                }

                if (string.IsNullOrEmpty(streamUpdate.Text))
                {
                    continue;
                }

                var containsProtocolLeak = false;
                lock (state.SyncRoot)
                {
                    state.Content.Append(streamUpdate.Text);
                    containsProtocolLeak = AgentVisibleResponseGuard.ContainsProtocolLeak(state.Content.ToString());
                    if (containsProtocolLeak)
                    {
                        state.SuppressPendingFlush = true;
                    }
                    if (!containsProtocolLeak && ShouldFlushAssistantStream(state))
                    {
                        FlushAssistantStream(state);
                    }
                }

                if (containsProtocolLeak)
                {
                    protocolLeakDetected = true;
                    break;
                }
            }
        }
        finally
        {
            flushCancellation.Cancel();
            try
            {
                await flushTask;
            }
            catch (OperationCanceledException) when (flushCancellation.IsCancellationRequested)
            {
            }
        }

        if (protocolLeakDetected)
        {
            await BlockProtocolLeakAsync(state, cancellationToken);
        }
    }

    private static bool IsContentBearingOutput(AIContent content)
        => content switch
        {
            UsageContent => false,
            TextContent text => !string.IsNullOrEmpty(text.Text),
            TextReasoningContent reasoning => !string.IsNullOrEmpty(reasoning.Text),
            _ => true,
        };

    public AgentProviderCycleResult CompleteCycle(AgentStreamingTurnState state)
    {
        state.ReasoningActivity.Flush();
        if (state.TerminalResult is not null)
        {
            return new AgentProviderCycleResult(
                state.Content.ToString(),
                state.ToolCalls,
                state.TerminalResult,
                state.ProviderUsage?.ContextTokenCount);
        }

        lock (state.SyncRoot)
        {
            if (state.Content.Length > state.PersistedContentLength)
            {
                FlushAssistantStream(state);
            }
        }

        return new AgentProviderCycleResult(
            state.Content.ToString(),
            state.ToolCalls,
            TerminalResult: null,
            state.ProviderUsage?.ContextTokenCount);
    }

    private async Task BlockProtocolLeakAsync(
        AgentStreamingTurnState state,
        CancellationToken cancellationToken)
    {
        state.TerminalResult = await _terminalHandler.FailAsync(
            state.Host,
            state.AssistantTurnState,
            AgentVisibleResponseGuard.BlockedResponseContent,
            "Assistant response contained internal protocol syntax.",
            cancellationToken);
        state.Host.LogEvent(
            PackageLogLevel.Warning,
            "assistant.response.protocol_leak_blocked",
            "Assistant response contained internal protocol syntax.",
            state.LoopStopwatch.ElapsedMilliseconds,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["assistant.response_length"] = state.Content.Length,
            });
    }

    private static bool ShouldFlushAssistantStream(AgentStreamingTurnState state)
        => state.AssistantTurnState.Turn is null
           || state.LastAssistantFlushTimestamp is not { } lastFlushTimestamp
           || state.TimeProvider.GetElapsedTime(lastFlushTimestamp) >= AssistantStreamFlushInterval;

    private static void FlushAssistantStream(AgentStreamingTurnState state)
    {
        state.AssistantTurnState.Turn = state.Host.UpsertAssistantTurn(
            state.AssistantTurnState.Turn,
            state.Content.ToString());
        state.PersistedContentLength = state.Content.Length;
        state.LastAssistantFlushTimestamp = state.TimeProvider.GetTimestamp();
    }

    private static async Task FlushPendingTextAsync(
        AgentStreamingTurnState state,
        CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(AssistantStreamFlushInterval, state.TimeProvider);
        while (await timer.WaitForNextTickAsync(cancellationToken))
        {
            lock (state.SyncRoot)
            {
                if (state.Host.IsCurrentRun()
                    && !state.SuppressPendingFlush
                    && state.Content.Length > state.PersistedContentLength
                    && ShouldFlushAssistantStream(state))
                {
                    FlushAssistantStream(state);
                }
            }
        }
    }
}

internal sealed class AgentStreamingTurnState(
    IAgentBehaviorLoopRuntime host,
    AgentBehaviorLoopContext context,
    AgentAssistantTurnState assistantTurnState,
    Stopwatch loopStopwatch,
    TimeProvider timeProvider)
{
    public IAgentBehaviorLoopRuntime Host { get; } = host;

    public AgentBehaviorLoopContext Context { get; } = context;

    public AgentAssistantTurnState AssistantTurnState { get; } = assistantTurnState;

    public Stopwatch LoopStopwatch { get; } = loopStopwatch;

    public TimeProvider TimeProvider { get; } = timeProvider;

    public StringBuilder Content { get; } = new();

    public object SyncRoot { get; } = new();

    public List<FunctionCallContent> ToolCalls { get; } = [];

    public AgentProviderUsage? ProviderUsage { get; set; }

    public ReasoningActivityReporter ReasoningActivity { get; } = new(host as IAgentRunActivitySink);

    public long? LastAssistantFlushTimestamp { get; set; }

    public int PersistedContentLength { get; set; }

    public bool SuppressPendingFlush { get; set; }

    public bool HasContentBearingOutput { get; set; }

    public AgentBehaviorLoopResult? TerminalResult { get; set; }
}

internal sealed record AgentProviderCycleResult(
    string Text,
    IReadOnlyList<FunctionCallContent> ToolCalls,
    AgentBehaviorLoopResult? TerminalResult,
    long? ReportedContextTokenCount);

internal sealed record AgentProviderUsage(
    long? InputTokenCount,
    long? OutputTokenCount,
    long? TotalTokenCount)
{
    public long? ContextTokenCount => Normalize(TotalTokenCount)
        ?? Add(Normalize(InputTokenCount), Normalize(OutputTokenCount));

    public static AgentProviderUsage Merge(AgentProviderUsage? current, UsageDetails next)
        => new(
            next.InputTokenCount ?? current?.InputTokenCount,
            next.OutputTokenCount ?? current?.OutputTokenCount,
            next.TotalTokenCount ?? current?.TotalTokenCount);

    private static long? Normalize(long? value) => value is >= 0 ? value : null;

    private static long? Add(long? left, long? right)
    {
        if (left is null || right is null)
        {
            return null;
        }
        return left.Value > long.MaxValue - right.Value
            ? long.MaxValue
            : left.Value + right.Value;
    }
}

internal sealed class ReasoningActivityReporter(IAgentRunActivitySink? activitySink)
{
    private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(240);
    private const int MaxDisplayLines = 5;
    private const int MaxSegmentCharacters = 4096;
    private readonly IAgentRunActivitySink? _activitySink = activitySink;
    private readonly List<string> _completedSegments = [];
    private readonly StringBuilder _currentSegment = new();
    private TimeSpan _lastReportElapsed = TimeSpan.MinValue;
    private string _lastReportedText = string.Empty;

    public void Append(string? text, TimeSpan elapsed)
    {
        if (_activitySink is null || string.IsNullOrEmpty(text))
        {
            return;
        }

        AppendReasoningText(text);
        var displayText = CreateDisplayText();
        if (string.IsNullOrWhiteSpace(displayText)
            || string.Equals(displayText, _lastReportedText, StringComparison.Ordinal))
        {
            return;
        }

        if (_lastReportElapsed != TimeSpan.MinValue && elapsed - _lastReportElapsed < ReportInterval)
        {
            return;
        }

        Report(displayText, elapsed);
    }

    public void Flush()
    {
        if (_activitySink is null
            || (_completedSegments.Count == 0 && _currentSegment.Length == 0))
        {
            return;
        }

        var displayText = CreateDisplayText();
        if (!string.IsNullOrWhiteSpace(displayText)
            && !string.Equals(displayText, _lastReportedText, StringComparison.Ordinal))
        {
            Report(displayText, TimeSpan.MaxValue);
        }
    }

    private void Report(string displayText, TimeSpan elapsed)
    {
        _lastReportedText = displayText;
        _lastReportElapsed = elapsed;
        _activitySink?.ReportRunActivity(AgentRunActivityKind.Reasoning, displayText);
    }

    private void AppendReasoningText(string text)
    {
        foreach (var character in text)
        {
            if (character is '\r' or '\n')
            {
                CompleteCurrentSegment();
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                AppendCurrentSegmentSpace();
                continue;
            }

            if (_currentSegment.Length < MaxSegmentCharacters)
            {
                _currentSegment.Append(character);
            }

            if (IsSentenceTerminator(character))
            {
                CompleteCurrentSegment();
            }
        }
    }

    private void AppendCurrentSegmentSpace()
    {
        if (_currentSegment.Length == 0
            || _currentSegment.Length >= MaxSegmentCharacters
            || _currentSegment[^1] == ' ')
        {
            return;
        }

        _currentSegment.Append(' ');
    }

    private void CompleteCurrentSegment()
    {
        var segment = NormalizeSegment(_currentSegment.ToString());
        _currentSegment.Clear();
        if (string.IsNullOrWhiteSpace(segment) || !segment.Any(char.IsLetterOrDigit))
        {
            return;
        }

        _completedSegments.Add(segment);
        while (_completedSegments.Count > MaxDisplayLines)
        {
            _completedSegments.RemoveAt(0);
        }
    }

    private string CreateDisplayText()
    {
        var segments = new List<string>(_completedSegments);
        var currentSegment = NormalizeSegment(_currentSegment.ToString());
        if (!string.IsNullOrWhiteSpace(currentSegment)
            && currentSegment.Any(char.IsLetterOrDigit))
        {
            segments.Add(currentSegment);
        }

        return string.Join(
            Environment.NewLine,
            segments.Skip(Math.Max(0, segments.Count - MaxDisplayLines)));
    }

    private static string NormalizeSegment(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static bool IsSentenceTerminator(char character)
        => character is '.' or '?' or '!';
}
