using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Services;

namespace Sunder.Package.Agent.Services;

[SunderService(SunderServiceExposure.Public, Key = "child-run-executor", ContractId = "younics.ai.agent.child.run.executor", ServiceId = "child-run-executor", IncludeSchemaDialect = true)]
[SunderServiceProvider(ProviderId = "agent.child.runs")]
[SunderAppUse(Actions = ["discover", "invoke"])]
public sealed partial class AgentChildRunExecutor(
    AgentUserMessageRunCoordinator userMessageRunCoordinator,
    AgentChildRunSessionService childRunSessionService)
{
    [SunderMethod(TimeoutSeconds = 2100)]
    public async ValueTask<AgentChildRunResult> RunChildAsync(
        AgentChildRunRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var childRunSession = childRunSessionService.PrepareChildSession(request);
        var checkpoint = await userMessageRunCoordinator.QueueAsync(
            childRunSession.ChildSession.SessionId,
            childRunSession.ChildProfile.ProfileId,
            request.UserMessage,
            request.WorkspaceId,
            [],
            cancellationToken);
        return childRunSessionService.BuildResult(childRunSession.ChildSession, checkpoint);
    }
}
