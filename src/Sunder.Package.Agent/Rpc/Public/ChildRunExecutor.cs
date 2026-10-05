using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Domain = Sunder.Package.Agent.Contracts.Models;
using Mapping = Sunder.Package.Agent.Rpc.Public.Control.RunControlMapping;

namespace Sunder.Package.Agent.Rpc.Public;

[SunderRpcService(SunderRpcExposure.Public, ContractId = "sunder.agent.child.run.executor",
    ServiceId = "child-run-executor", IncludeSchemaDialect = true)]
[SunderRpcProvider(ProviderId = "agent.child.runs")]
public sealed partial class ChildRunExecutorService(IAgentChildRunExecutor executor)
{
    [SunderRpcMethod]
    public async ValueTask<AgentChildRunResult> RunChildAsync(AgentChildRunRequest request, CancellationToken cancellationToken = default)
    {
        var result = await executor.RunChildAsync(new Domain.AgentChildRunRequest(
            request.ParentSessionId.Value, request.ParentRunId.Value, request.ParentRunRevision.Value,
            request.ParentToolCallId.Value, request.WorkspaceId.Value, Mapping.Reference(request.TaskId, value => value.Value),
            Mapping.ToDomain(request.ChildProfile), request.UserMessage.Value, request.Title.Value, request.AgentKind.Value),
            cancellationToken).ConfigureAwait(false);
        return new()
        {
            Content = Mapping.FromReference(result.Content, value => new ContentText { Value = value }),
            SessionId = result.SessionId, Status = Enum.Parse<AgentRunStatus>(result.Status.ToString()), Summary = result.Summary,
            Title = DisplayText.Optional(result.Title),
        };
    }
}

public sealed class AgentChildRunRequest
{
    public required TextValue AgentKind { get; init; }
    public required AgentProfileRecord ChildProfile { get; init; }
    public required EntityId ParentRunId { get; init; }
    public required Revision ParentRunRevision { get; init; }
    public required EntityId ParentSessionId { get; init; }
    public required Identifier ParentToolCallId { get; init; }
    public SunderRpcOptional<Identifier> TaskId { get; init; }
    public required DisplayText Title { get; init; }
    public required ContentText UserMessage { get; init; }
    public required Identifier WorkspaceId { get; init; }
}

public sealed class AgentChildRunResult
{
    public SunderRpcOptional<ContentText> Content { get; init; }
    public required EntityId SessionId { get; init; }
    public required AgentRunStatus Status { get; init; }
    public required DisplayText Summary { get; init; }
    public SunderRpcOptional<DisplayText> Title { get; init; }
}
