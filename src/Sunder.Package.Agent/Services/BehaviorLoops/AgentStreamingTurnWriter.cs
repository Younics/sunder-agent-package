using System.Diagnostics;
using System.Text;
using System.Threading.Channels;
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
        AgentTurnRecord? turnToReset;
        lock (state.SyncRoot)
        {
            turnToReset = state.Content.Length > 0
                ? state.AssistantTurnState.Turn
                : null;
        }

        var resetTurn = turnToReset is null
            ? null
            : state.Host.UpsertAssistantTurn(turnToReset, string.Empty);

        lock (state.SyncRoot)
        {
            state.AssistantTurnState.Turn = resetTurn ?? state.AssistantTurnState.Turn;
            state.Content.Clear();
            state.ContentGeneration = checked(state.ContentGeneration + 1);
            state.PersistedContentGeneration = state.ContentGeneration;
            state.ToolCalls.Clear();
            state.ProviderUsage = null;
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
        using var streamCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var flushPump = new AssistantStreamFlushPump(state, streamCancellation);
        var cancellationRegistration = cancellationToken.UnsafeRegister(
            static pump => ((AssistantStreamFlushPump)pump!).Abort(),
            flushPump);
        var protocolLeakDetected = false;
        var flushFinalSnapshot = false;
        try
        {
            await foreach (var streamUpdate in chatClient.GetStreamingResponseAsync(
                               promptMessages,
                               chatOptions,
                               streamCancellation.Token)
                               .WithCancellation(streamCancellation.Token))
            {
                cancellationToken.ThrowIfCancellationRequested();

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
                    state.ContentGeneration = checked(state.ContentGeneration + 1);
                    containsProtocolLeak = AgentVisibleResponseGuard.ContainsProtocolLeak(state.Content.ToString());
                    if (containsProtocolLeak)
                    {
                        state.SuppressPendingFlush = true;
                    }
                }

                if (containsProtocolLeak)
                {
                    protocolLeakDetected = true;
                    break;
                }

                flushPump.Schedule(cancellationToken);
            }

            if (!protocolLeakDetected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                flushFinalSnapshot = true;
            }
        }
        finally
        {
            try
            {
                await flushPump.StopAsync(flushFinalSnapshot);
            }
            finally
            {
                cancellationRegistration.Dispose();
                flushPump.Dispose();
            }
        }

        if (protocolLeakDetected)
        {
            await BlockProtocolLeakAsync(state, cancellationToken);
        }
        else
        {
            cancellationToken.ThrowIfCancellationRequested();
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

    private sealed class AssistantStreamFlushPump : IDisposable
    {
        private readonly AgentStreamingTurnState _state;
        private readonly Channel<bool> _signals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
        {
            AllowSynchronousContinuations = false,
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false,
        });
        private readonly CancellationTokenSource _waitCancellation = new();
        private readonly object _lifecycleSync = new();
        private readonly Task _worker;
        private FlushPumpStopMode _stopMode;
        private long? _lastFlushTimestamp;

        public AssistantStreamFlushPump(
            AgentStreamingTurnState state,
            CancellationTokenSource streamCancellation)
        {
            _state = state;
            _streamCancellation = streamCancellation;
            _worker = Task.Run(RunAsync);
        }

        private readonly CancellationTokenSource _streamCancellation;

        public void Schedule(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var observeWorker = false;
            lock (_lifecycleSync)
            {
                if (_stopMode == FlushPumpStopMode.Running && !_worker.IsCompleted)
                {
                    _signals.Writer.TryWrite(true);
                    return;
                }

                observeWorker = _worker.IsCompleted;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (observeWorker)
            {
                _worker.GetAwaiter().GetResult();
            }

            throw new InvalidOperationException("The assistant stream flush pump is no longer accepting updates.");
        }

        public void Abort() => RequestStop(FlushPumpStopMode.Abort);

        public async Task StopAsync(bool flushFinalSnapshot)
        {
            RequestStop(flushFinalSnapshot
                ? FlushPumpStopMode.FlushFinal
                : FlushPumpStopMode.Abort);
            await _worker;
        }

        public void Dispose() => _waitCancellation.Dispose();

        private async Task RunAsync()
        {
            try
            {
                try
                {
                    while (GetStopMode() == FlushPumpStopMode.Running
                           && await _signals.Reader.WaitToReadAsync(_waitCancellation.Token))
                    {
                        DrainSignals();
                        if (GetStopMode() != FlushPumpStopMode.Running)
                        {
                            break;
                        }

                        await WaitForCadenceAsync();
                        DrainSignals();
                        if (TryCaptureSnapshot(out var snapshot)
                            && TryBeginPersistence(FlushPumpStopMode.Running))
                        {
                            Persist(snapshot);
                        }
                    }
                }
                catch (OperationCanceledException ex) when (
                    _waitCancellation.IsCancellationRequested
                    && ex.CancellationToken == _waitCancellation.Token)
                {
                }

                if (GetStopMode() == FlushPumpStopMode.FlushFinal
                    && TryCaptureSnapshot(out var finalSnapshot)
                    && TryBeginPersistence(FlushPumpStopMode.FlushFinal))
                {
                    Persist(finalSnapshot);
                }
            }
            catch
            {
                try
                {
                    _streamCancellation.Cancel();
                }
                catch
                {
                    // Preserve the durable persistence failure surfaced by the pump.
                }
                throw;
            }
            finally
            {
                _signals.Writer.TryComplete();
            }
        }

        private async Task WaitForCadenceAsync()
        {
            if (_lastFlushTimestamp is not { } lastFlushTimestamp)
            {
                return;
            }

            var remaining = AssistantStreamFlushInterval
                - _state.TimeProvider.GetElapsedTime(lastFlushTimestamp);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining, _state.TimeProvider, _waitCancellation.Token);
            }
        }

        private bool TryCaptureSnapshot(out AssistantStreamSnapshot snapshot)
        {
            lock (_state.SyncRoot)
            {
                if (_state.SuppressPendingFlush
                    || _state.ContentGeneration <= _state.PersistedContentGeneration)
                {
                    snapshot = default;
                    return false;
                }

                snapshot = new AssistantStreamSnapshot(
                    _state.AssistantTurnState.Turn,
                    _state.Content.ToString(),
                    _state.ContentGeneration);
                return true;
            }
        }

        private bool TryBeginPersistence(FlushPumpStopMode requiredMode)
        {
            lock (_lifecycleSync)
            {
                return _stopMode == requiredMode;
            }
        }

        private FlushPumpStopMode GetStopMode()
        {
            lock (_lifecycleSync)
            {
                return _stopMode;
            }
        }

        private void Persist(AssistantStreamSnapshot snapshot)
        {
            var persistedTurn = _state.Host.UpsertAssistantTurn(snapshot.Turn, snapshot.Content);
            var flushTimestamp = _state.TimeProvider.GetTimestamp();
            lock (_state.SyncRoot)
            {
                if (snapshot.Generation > _state.PersistedContentGeneration)
                {
                    _state.AssistantTurnState.Turn = persistedTurn;
                    _state.PersistedContentGeneration = snapshot.Generation;
                }
            }

            _lastFlushTimestamp = flushTimestamp;
        }

        private void RequestStop(FlushPumpStopMode stopMode)
        {
            var interruptWait = false;
            lock (_lifecycleSync)
            {
                if (_stopMode == FlushPumpStopMode.Abort
                    || _stopMode == stopMode)
                {
                    return;
                }

                if (_stopMode == FlushPumpStopMode.FlushFinal
                    && stopMode != FlushPumpStopMode.Abort)
                {
                    return;
                }

                _stopMode = stopMode;
                interruptWait = true;
            }

            if (interruptWait)
            {
                _signals.Writer.TryComplete();
                _waitCancellation.Cancel();
            }
        }

        private void DrainSignals()
        {
            while (_signals.Reader.TryRead(out _))
            {
            }
        }

        private readonly record struct AssistantStreamSnapshot(
            AgentTurnRecord? Turn,
            string Content,
            long Generation);

        private enum FlushPumpStopMode
        {
            Running,
            FlushFinal,
            Abort,
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

    public long ContentGeneration { get; set; }

    public long PersistedContentGeneration { get; set; }

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
