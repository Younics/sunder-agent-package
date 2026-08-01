using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class AgentBehaviorLoopResolver(
    AgentRpcCatalog rpcCatalog,
    AgentRpcProviderService<IAgentBehaviorLoop> behaviorLoops)
{
    public AgentBehaviorLoopSelection Resolve(AgentProfileRecord profile)
    {
        var requestedLoopId = string.IsNullOrWhiteSpace(profile.BehaviorLoopId)
            ? DefaultAgentBehaviorLoop.LoopId
            : profile.BehaviorLoopId.Trim();
        var requestedSourceId = string.IsNullOrWhiteSpace(profile.BehaviorLoopSourceId)
            ? null
            : profile.BehaviorLoopSourceId.Trim();

        var loops = AgentRpcInvocation.Snapshot(
            rpcCatalog,
            behaviorLoops,
            static loop => loop.Descriptor with
            {
                FeatureKinds = loop.Descriptor.FeatureKinds?.ToArray(),
            });
        var selected = loops.FirstOrDefault(loop =>
                           string.Equals(
                               loop.Metadata.LoopId,
                               requestedLoopId,
                               StringComparison.OrdinalIgnoreCase)
                           && (requestedSourceId is null
                               || string.Equals(
                                   loop.Metadata.SourceId,
                                   requestedSourceId,
                                   StringComparison.OrdinalIgnoreCase)))
                       ?? loops.FirstOrDefault(loop => string.Equals(
                           loop.Metadata.LoopId,
                           DefaultAgentBehaviorLoop.LoopId,
                           StringComparison.OrdinalIgnoreCase));
        if (selected is not null && selected.Reference.TryAcquire(out var lease))
        {
            return new AgentBehaviorLoopSelection(selected.Metadata, selected.PackageId, lease);
        }

        throw AgentRpcInvocation.Unavailable("sunder.package.agent");
    }
}

public sealed class AgentBehaviorLoopSelection : IDisposable
{
    private AgentRpcLease<IAgentBehaviorLoop>? _lease;

    internal AgentBehaviorLoopSelection(
        AgentBehaviorLoopDescriptor descriptor,
        string ownerPackageId,
        AgentRpcLease<IAgentBehaviorLoop> lease)
    {
        Descriptor = descriptor;
        OwnerPackageId = ownerPackageId;
        _lease = lease;
        RetirementToken = lease.RetirementToken;
    }

    public AgentBehaviorLoopDescriptor Descriptor { get; }

    internal string? OwnerPackageId { get; }

    internal CancellationToken RetirementToken { get; }

    internal bool IsRetiring => RetirementToken.IsCancellationRequested;

    internal async ValueTask<AgentBehaviorLoopResult> RunAsync(
        AgentBehaviorLoopContext context,
        IAgentBehaviorLoopRuntime runtime,
        CancellationToken cancellationToken)
    {
        var lease = Volatile.Read(ref _lease);
        if (lease is null)
        {
            throw new ObjectDisposedException(nameof(AgentBehaviorLoopSelection));
        }

        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            RetirementToken);
        try
        {
            var result = await lease.Service
                .RunAsync(context, runtime, invocation.Token).ConfigureAwait(false);
            if (IsRetiring && !cancellationToken.IsCancellationRequested)
            {
                throw AgentRpcInvocation.Unavailable(OwnerPackageId!);
            }

            return result;
        }
        catch (AgentPackageUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException exception) when (
            IsRetiring
            && !cancellationToken.IsCancellationRequested)
        {
            throw AgentRpcInvocation.Unavailable(OwnerPackageId!, exception);
        }
        catch (Exception exception) when (
            IsRetiring
            && !cancellationToken.IsCancellationRequested)
        {
            throw AgentRpcInvocation.Unavailable(OwnerPackageId!, exception);
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
