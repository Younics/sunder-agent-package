using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class AgentBehaviorLoopResolver(
    IPackageExtensionCatalog extensionCatalog,
    DefaultAgentBehaviorLoop defaultBehaviorLoop)
{
    private readonly IPackageExtensionCatalog _extensionCatalog = extensionCatalog;
    private readonly DefaultAgentBehaviorLoop _defaultBehaviorLoop = defaultBehaviorLoop;

    public IAgentBehaviorLoop Resolve(AgentProfileRecord profile)
    {
        var requestedLoopId = string.IsNullOrWhiteSpace(profile.BehaviorLoopId)
            ? DefaultAgentBehaviorLoop.LoopId
            : profile.BehaviorLoopId.Trim();
        var requestedSourceId = string.IsNullOrWhiteSpace(profile.BehaviorLoopSourceId)
            ? null
            : profile.BehaviorLoopSourceId.Trim();

        var loops = _extensionCatalog.GetExtensions(PackageExtensionPoints.BehaviorLoops);
        return loops.FirstOrDefault(loop => string.Equals(loop.Descriptor.LoopId, requestedLoopId, StringComparison.OrdinalIgnoreCase)
                                            && (requestedSourceId is null
                                                || string.Equals(loop.Descriptor.SourceId, requestedSourceId, StringComparison.OrdinalIgnoreCase)))
               ?? loops.FirstOrDefault(loop => string.Equals(loop.Descriptor.LoopId, DefaultAgentBehaviorLoop.LoopId, StringComparison.OrdinalIgnoreCase))
               ?? _defaultBehaviorLoop;
    }
}
