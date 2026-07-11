using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services;

public sealed class AgentToolPresentationService
{
    private readonly TranscriptToolPresentationService _shared;

    public AgentToolPresentationService(
        InstalledPackageToolSource? installedPackageToolSource = null,
        IPackageExtensionCatalog? extensionCatalog = null)
    {
        _shared = new TranscriptToolPresentationService(() =>
        {
            var resolvers = new List<IAgentToolPresentationResolver>();
            if (installedPackageToolSource is not null)
            {
                resolvers.Add(installedPackageToolSource);
            }
            if (extensionCatalog is not null)
            {
                resolvers.AddRange(extensionCatalog
                    .GetExtensions(PackageExtensionPoints.ToolSources)
                    .OfType<IAgentToolPresentationResolver>());
            }

            return resolvers;
        });
    }

    public AgentToolPresentation Resolve(AgentTurnItemRecord item) => _shared.Resolve(item);
}
