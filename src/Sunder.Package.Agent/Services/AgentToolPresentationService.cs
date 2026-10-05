using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Shared.PackageViews;

namespace Sunder.Package.Agent.Services;

public sealed partial class AgentToolPresentationService
{
    private readonly TranscriptToolPresentationService _shared;

    public AgentToolPresentationService(
        AgentRpcCatalog? rpcCatalog = null)
    {
        _shared = new TranscriptToolPresentationService(() =>
        {
            return rpcCatalog is null
                ? []
                : rpcCatalog.GetServiceReferences(Sunder.Package.Agent.Runtime.Rpc.AgentToolBindings.Service)
                    .Select(static reference =>
                        (IAgentToolPresentationResolver)new RpcToolSourcePresentationResolver(reference))
                    .ToArray();
        });
    }

    public AgentToolPresentation Resolve(AgentTurnItemRecord item) => _shared.Resolve(item);
}

internal sealed class RpcToolSourcePresentationResolver(
    AgentRpcReference<IAgentToolSource> reference) : IAgentToolPresentationResolver
{
    public AgentToolPresentation? ResolveToolPresentation(AgentToolPresentationRequest request)
    {
        if (!reference.TryAcquire(out var lease))
        {
            return null;
        }

        using (lease)
        {
            var presentation = lease.Service is IAgentToolPresentationResolver resolver
                ? resolver.ResolveToolPresentation(request)
                : null;
            return lease.RetirementToken.IsCancellationRequested ? null : presentation;
        }
    }
}
