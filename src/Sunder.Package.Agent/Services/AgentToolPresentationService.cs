using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Services;

public sealed class AgentToolPresentationService
{
    private readonly TranscriptToolPresentationService _shared;

    public AgentToolPresentationService(
        AgentRpcCatalog? rpcCatalog = null)
    {
        _shared = new TranscriptToolPresentationService(() =>
        {
            return rpcCatalog is null
                ? []
                : rpcCatalog.GetServiceReferences(AgentRpcServices.ToolSources)
                    .Select(static reference =>
                        (IAgentToolPresentationResolver)new RpcToolSourcePresentationResolver(reference))
                    .ToArray();
        });
    }

    public AgentToolPresentation Resolve(AgentTurnItemRecord item) => _shared.Resolve(item);
}
