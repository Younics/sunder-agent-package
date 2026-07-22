using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Agent.Extension.Minimal;

public sealed class CurrentUtcTimeTool : IAgentTool
{
    public AgentToolDescriptor Descriptor { get; } = new(
        "sample_current_utc_time",
        "Current UTC Time",
        "Returns the current UTC timestamp in ISO 8601 format.",
        ArgumentsJsonSchema: """
        {
          "type": "object",
          "properties": {},
          "additionalProperties": false
        }
        """,
        SourceKind: "sample",
        SourceId: "sample.sunder.agent.extension.minimal",
        SourceDisplayName: "Minimal Agent Extension")
    {
        ConcurrencyMode = AgentToolConcurrencyMode.ParallelSafe,
    };

    public ValueTask<AgentToolReadiness> GetReadinessAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AgentToolReadiness(
            Descriptor.ToolId,
            AgentToolReadinessStatus.Ready,
            "The in-process UTC clock is available."));
    }

    public ValueTask<AgentToolResult> ExecuteAsync(
        AgentToolExecutionContext context,
        AgentToolRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AgentToolArgumentObject.TryParse(request.ArgumentsJson, out var arguments, out var error)
            || arguments!.ToDictionary().Count != 0)
        {
            return ValueTask.FromResult(new AgentToolResult(
                Descriptor.ToolId,
                error ?? "This tool does not accept arguments.",
                IsError: true,
                ErrorCode: "sample-arguments-invalid"));
        }

        var timestamp = DateTimeOffset.UtcNow.ToString("O");
        return ValueTask.FromResult(new AgentToolResult(
            Descriptor.ToolId,
            $"Current UTC time: {timestamp}",
            Content: timestamp));
    }
}
