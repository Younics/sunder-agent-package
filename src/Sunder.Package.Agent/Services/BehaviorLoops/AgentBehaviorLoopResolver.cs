using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class AgentBehaviorLoopResolver(
    IServiceProviders<AgentServices.IBehaviorLoopService> behaviorLoops,
    IServiceProviders<AgentServices.IRunControlService> runControls,
    AgentActiveRunRegistry activeRuns,
    DefaultAgentBehaviorLoop defaultBehaviorLoop)
{
    public async ValueTask<AgentBehaviorLoopSelection> ResolveAsync(
        AgentProfileRecord profile, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var requestedLoopId = string.IsNullOrWhiteSpace(profile.BehaviorLoopId)
            ? DefaultAgentBehaviorLoop.LoopId : profile.BehaviorLoopId.Trim();
        var requestedSourceId = string.IsNullOrWhiteSpace(profile.BehaviorLoopSourceId)
            ? null : profile.BehaviorLoopSourceId.Trim();
        if (string.Equals(requestedLoopId, DefaultAgentBehaviorLoop.LoopId, StringComparison.OrdinalIgnoreCase)
                && requestedSourceId is null
            || string.Equals(requestedSourceId, DefaultAgentBehaviorLoop.BuiltInSourceId, StringComparison.OrdinalIgnoreCase))
            return new(defaultBehaviorLoop);

        ServiceProviderCatalog<AgentServices.IBehaviorLoopService> catalog;
        try
        {
            catalog = await behaviorLoops.ListAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
            && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            return new(defaultBehaviorLoop);
        }

        foreach (var provider in catalog.Providers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (provider.State != ServiceProviderState.Active) continue;
            try
            {
                var descriptor = await provider.Service.DescribeAsync(cancellationToken).ConfigureAwait(false);
                if (string.Equals(descriptor.LoopId, requestedLoopId, StringComparison.OrdinalIgnoreCase)
                    && (requestedSourceId is null || string.Equals(descriptor.SourceId, requestedSourceId, StringComparison.OrdinalIgnoreCase)))
                    return new(descriptor, provider, runControls, activeRuns);
            }
            catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
                && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
            {
                // A missing selected extension falls back to the built-in loop.
            }
        }
        return new(defaultBehaviorLoop);
    }
}

public sealed class AgentBehaviorLoopSelection
{
    private readonly DefaultAgentBehaviorLoop? _local;
    private readonly ServiceProvider<AgentServices.IBehaviorLoopService>? _provider;
    private readonly IServiceProviders<AgentServices.IRunControlService>? _runControls;
    private readonly AgentActiveRunRegistry? _activeRuns;

    internal AgentBehaviorLoopSelection(DefaultAgentBehaviorLoop local)
    {
        _local = local;
        Descriptor = local.Descriptor;
        OwnerPackageId = DefaultAgentBehaviorLoop.BuiltInSourceId;
    }

    internal AgentBehaviorLoopSelection(AgentBehaviorLoopDescriptor descriptor,
        ServiceProvider<AgentServices.IBehaviorLoopService> provider,
        IServiceProviders<AgentServices.IRunControlService> runControls,
        AgentActiveRunRegistry activeRuns)
    {
        Descriptor = descriptor;
        OwnerPackageId = provider.PackageId;
        _provider = provider;
        _runControls = runControls;
        _activeRuns = activeRuns;
    }

    public AgentBehaviorLoopDescriptor Descriptor { get; }
    internal string OwnerPackageId { get; }

    internal async ValueTask<AgentBehaviorLoopResult> RunAsync(AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime runtime, CancellationToken cancellationToken)
    {
        if (_local is not null)
            return await _local.RunAsync(context, runtime, cancellationToken).ConfigureAwait(false);

        var control = (await _runControls!.ListAsync(cancellationToken).ConfigureAwait(false)).Providers
            .SingleOrDefault(provider => provider.State == ServiceProviderState.Active
                && provider.PackageId == "sunder.package.agent" && provider.ProviderId == "agent.run.control")
            ?? throw new AgentPackageUnavailableException("sunder.package.agent");
        using var grant = _activeRuns!.OpenBehaviorInvocation(context.Session.SessionId, context.RunId,
            context.RunRevision, runtime, OwnerPackageId);
        try
        {
            return await _provider!.Service.RunAsync(context, grant.InvocationId, control.Reference, cancellationToken).ConfigureAwait(false);
        }
        catch (SunderRpcException exception) when (!cancellationToken.IsCancellationRequested
            && exception.Error.Kind is SunderRpcErrorKind.StaleEndpoint or SunderRpcErrorKind.Unavailable)
        {
            throw new AgentPackageUnavailableException(OwnerPackageId, exception);
        }
    }
}
