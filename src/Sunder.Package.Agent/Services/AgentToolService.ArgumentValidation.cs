using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolService
{
    internal async ValueTask<AgentToolResult?> ValidateArgumentsAsync(
        AgentToolCallRequest call,
        AgentToolInvocationReference invocation,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!AgentToolArgumentObject.TryParse(call.ArgumentsJson, out _, out var error))
        {
            return new(call.ToolId, "Invalid tool arguments.", Content: error,
                IsError: true, ErrorCode: "tool-arguments-invalid");
        }

        var result = await invocation.SourceProvider.Service.ValidateArgumentsAsync(
            new AgentToolRequest(call.ToolId, call.ArgumentsJson), cancellationToken).ConfigureAwait(false);
        if (result is null) return null;
        return result with
        {
            ToolId = call.ToolId,
            IsError = true,
            ErrorCode = string.IsNullOrWhiteSpace(result.ErrorCode) ? "tool-arguments-invalid" : result.ErrorCode,
        };
    }
}
