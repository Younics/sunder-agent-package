using System.Diagnostics.CodeAnalysis;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed class AgentRunProgressGuard
{
    private const int MaxConsecutiveIdenticalToolOutcomes = 16;
    private const int CorrectionThreshold = MaxConsecutiveIdenticalToolOutcomes / 2;
    internal const string CorrectionInstruction =
        "The same tool invocation has repeatedly returned the same error. Review its recorded error and change the approach "
        + "or explain the blocker. Do not repeat an unchanged failed invocation. This notice grants no additional permission.";

    private string? _lastToolOutcomeSignature;
    private int _consecutiveIdenticalToolOutcomeCount;

    public bool RecordToolOutcome(
        AgentToolCallRequest toolCall,
        AgentToolCallOutcome outcome,
        [NotNullWhen(true)] out AgentRunProgressFailure? failure)
    {
        failure = null;
        // Success, including an unchanged observation of a running operation, is
        // not evidence of a failed loop. Never guess progress from tool names.
        if (outcome.Kind != AgentToolCallOutcomeKind.Executed || outcome.Result?.IsError != true)
        {
            _lastToolOutcomeSignature = null;
            _consecutiveIdenticalToolOutcomeCount = 0;
            return false;
        }
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

        if (_consecutiveIdenticalToolOutcomeCount != CorrectionThreshold
            && _consecutiveIdenticalToolOutcomeCount < MaxConsecutiveIdenticalToolOutcomes)
        {
            return false;
        }

        failure = new AgentRunProgressFailure(
            "### Agent needs guidance\n\nThe agent kept repeating the same failed tool invocation after corrective feedback. Completed work is saved. Continue with a different approach or clarify the blocker.",
            "Agent repeated the same failed tool invocation after corrective feedback.",
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["tool.id"] = toolCall.ToolId,
                ["tool.call_id"] = toolCall.CallId,
                ["tool.repeat_count"] = _consecutiveIdenticalToolOutcomeCount,
            },
            IsCorrection: _consecutiveIdenticalToolOutcomeCount == CorrectionThreshold);
        return true;
    }

    private static string BuildToolOutcomeSignature(AgentToolCallRequest toolCall, AgentToolCallOutcome outcome)
    {
        var result = outcome.Result;
        var text = JsonSerializer.Serialize(new
        {
            Invocation = AgentToolInvocationFingerprint.Create(toolCall.ToolId, toolCall.ArgumentsJson),
            outcome.Kind,
            result?.ErrorCode,
            result?.Summary,
            result?.Content,
            result?.StructuredPayloadJson,
            result?.Sources,
            result?.BackendId,
            result?.WasTruncated,
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

}

internal sealed record AgentRunProgressFailure(
    string VisibleMessage,
    string CheckpointSummary,
    IReadOnlyDictionary<string, object?> Attributes,
    bool IsCorrection);
