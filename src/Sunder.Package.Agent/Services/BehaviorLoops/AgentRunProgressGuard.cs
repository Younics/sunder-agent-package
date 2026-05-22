using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentRunProgressGuard
{
    private const int MaxConsecutiveIdenticalToolOutcomes = 16;
    private const int MaxOutcomeFingerprintChars = 4_096;

    private string? _lastToolOutcomeSignature;
    private int _consecutiveIdenticalToolOutcomeCount;

    public bool RecordToolOutcome(
        AgentToolCallRequest toolCall,
        AgentToolCallOutcome outcome,
        [NotNullWhen(true)] out AgentRunProgressFailure? failure)
    {
        failure = null;
        var signature = BuildToolOutcomeSignature(toolCall, outcome);
        if (string.Equals(signature, _lastToolOutcomeSignature, StringComparison.Ordinal))
        {
            _consecutiveIdenticalToolOutcomeCount++;
        }
        else
        {
            _lastToolOutcomeSignature = signature;
            _consecutiveIdenticalToolOutcomeCount = 1;
        }

        if (_consecutiveIdenticalToolOutcomeCount < MaxConsecutiveIdenticalToolOutcomes)
        {
            return false;
        }

        failure = new AgentRunProgressFailure(
            "### Agent run stopped\n\nThe agent repeated the same tool call and received the same result many times in a row. This usually means the run is stuck rather than making progress. Please adjust the request or continue with more specific guidance.",
            "Agent repeated the same tool call result without progress.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = toolCall.ToolId,
                ["tool.call_id"] = toolCall.CallId,
                ["tool.repeat_count"] = _consecutiveIdenticalToolOutcomeCount,
            });
        return true;
    }

    private static string BuildToolOutcomeSignature(AgentToolCallRequest toolCall, AgentToolCallOutcome outcome)
    {
        var result = outcome.Result;
        var text = string.Concat(
            toolCall.ToolId,
            '\n',
            NormalizeJsonish(toolCall.ArgumentsJson),
            '\n',
            outcome.Kind,
            '\n',
            result?.IsError,
            '\n',
            result?.ErrorCode,
            '\n',
            result?.Summary,
            '\n',
            Truncate(result?.Content ?? string.Empty, MaxOutcomeFingerprintChars));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static string NormalizeJsonish(string value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value.Trim();

    private static string Truncate(string value, int maxChars)
        => value.Length <= maxChars ? value : value[..maxChars];
}

internal sealed record AgentRunProgressFailure(
    string VisibleMessage,
    string CheckpointSummary,
    IReadOnlyDictionary<string, object?> Attributes);
