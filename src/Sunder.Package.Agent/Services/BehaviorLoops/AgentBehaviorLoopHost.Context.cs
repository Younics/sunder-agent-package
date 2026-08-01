using System.Diagnostics;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentBehaviorLoopHost
{
    private AgentSessionPromptProjection? _selectedSessionContextProjection;
    private long _promptContextTranscriptEpoch;

    void IAgentSessionContextSelectionRuntime.SelectSessionContextProjection(
        AgentSessionPromptProjection projection)
    {
        _selectedSessionContextProjection = projection;
    }

    ValueTask IAgentPromptContextAcknowledgmentRuntime.AcknowledgePromptContextAsync(
        IReadOnlyList<AgentPromptContextReceiptBlock> blocks,
        CancellationToken cancellationToken)
        => _memoryCoordinator.AcknowledgePromptContextAsync(
            new AgentPromptContextReceipt(
                _session.SessionId,
                _runId,
                _promptContextTranscriptEpoch,
                blocks),
            cancellationToken);

    void IAgentPromptContextAcknowledgmentRuntime.DiscardPromptContextAcknowledgment()
        => _memoryCoordinator.DiscardPromptContextAcknowledgment(
            _session.SessionId,
            _runId,
            _promptContextTranscriptEpoch);

    public async ValueTask<AgentBehaviorInstructionContext> BuildInstructionContextAsync(
        CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        LogEvent(PackageLogLevel.Debug, "memory.context.start", "Building memory context.");
        try
        {
            var context = _selectedSessionContextProjection is { } projection
                ? await _memoryCoordinator.BuildInstructionContextForProjectionAsync(
                    _session,
                    _profile,
                    _runId,
                    _runRevision,
                    _userMessage,
                    _runStartedAtUtc,
                    _workspace,
                    _executionBinding,
                    _availableToolsById?.Values.ToArray() ?? [],
                    projection.ContextCheckpoint,
                    projection.PromptTurns,
                    cancellationToken)
                : await _memoryCoordinator.BuildInstructionContextAsync(
                    _session,
                    _profile,
                    _runId,
                    _runRevision,
                    _userMessage,
                    _runStartedAtUtc,
                    _workspace,
                    _executionBinding,
                    _availableToolsById?.Values.ToArray() ?? [],
                    cancellationToken);

            LogEvent(
                PackageLogLevel.Debug,
                "memory.context.completed",
                context.HasSupplementaryContext ? "supplementary context included" : "no supplementary context",
                stopwatch.ElapsedMilliseconds,
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["memory.has_supplementary_context"] = context.HasSupplementaryContext,
                    ["memory.system_instruction_length"] = context.SystemInstructions?.Length ?? 0,
                    ["memory.prompt_context_block_count"] = context.PromptContextBlocks?.Count ?? 0,
                    ["memory.recall_entry_count"] = context.RecallResult?.Entries.Count ?? 0,
                });
            _promptContextTranscriptEpoch = context.TranscriptEpoch;
            return new AgentBehaviorInstructionContext(
                context.SystemInstructions,
                context.HasSupplementaryContext,
                context.PromptContextBlocks);
        }
        catch (OperationCanceledException)
        {
            LogEvent(
                PackageLogLevel.Debug,
                "memory.context.canceled",
                "Memory context build was canceled.",
                elapsedMilliseconds: stopwatch.ElapsedMilliseconds);
            throw;
        }
        catch (Exception ex)
        {
            LogEvent(
                PackageLogLevel.Warning,
                "memory.context.failed",
                "Memory context build failed.",
                stopwatch.ElapsedMilliseconds,
                exception: ex);
            throw;
        }
    }

    private static IReadOnlyList<string> GetResourceReferences(AgentPermissionRequest? request)
    {
        if (request is null)
        {
            return [];
        }

        return request.ResourceReferences
            .Concat(string.IsNullOrWhiteSpace(request.ResourceReference) ? [] : [request.ResourceReference])
            .Where(reference => !string.IsNullOrWhiteSpace(reference))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static IReadOnlyList<AgentResourceClaim> GetResourceClaims(AgentPermissionRequest? request)
        => request?.ResourceClaims
            .Where(static claim => claim is not null)
            .DistinctBy(static claim => (claim.NamespaceId, claim.ResourceIndex, claim.LogicalPath))
            .OrderBy(static claim => claim.ResourceIndex)
            .ToArray() ?? [];

    private static IReadOnlyList<string> GetResourceCapabilities(AgentPermissionRequest? request)
        => request?.ResourceCapabilities
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
}
