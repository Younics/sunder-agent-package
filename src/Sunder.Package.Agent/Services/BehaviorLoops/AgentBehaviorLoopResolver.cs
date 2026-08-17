using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class AgentBehaviorLoopResolver(
    AgentRpcCatalog rpcCatalog,
    AgentRpcProviderService<IAgentBehaviorLoop> behaviorLoops,
    DefaultAgentBehaviorLoop defaultBehaviorLoop)
{
    public AgentBehaviorLoopSelection Resolve(AgentProfileRecord profile)
    {
        var requestedLoopId = string.IsNullOrWhiteSpace(profile.BehaviorLoopId)
            ? DefaultAgentBehaviorLoop.LoopId
            : profile.BehaviorLoopId.Trim();
        var requestedSourceId = string.IsNullOrWhiteSpace(profile.BehaviorLoopSourceId)
            ? null
            : profile.BehaviorLoopSourceId.Trim();

        if ((string.Equals(
                 requestedLoopId,
                 DefaultAgentBehaviorLoop.LoopId,
                 StringComparison.OrdinalIgnoreCase)
             && requestedSourceId is null)
            || string.Equals(
                requestedSourceId,
                DefaultAgentBehaviorLoop.BuiltInSourceId,
                StringComparison.OrdinalIgnoreCase))
        {
            return new AgentBehaviorLoopSelection(defaultBehaviorLoop);
        }

        IReadOnlyList<AgentRpcOwnedProviderReference<IAgentBehaviorLoop, AgentBehaviorLoopDescriptor>> loops;
        try
        {
            loops = AgentRpcInvocation.Snapshot(
                rpcCatalog,
                behaviorLoops,
                static loop => loop.Descriptor with
                {
                    FeatureKinds = loop.Descriptor.FeatureKinds?.ToArray(),
                },
                omitUnavailable: true);
        }
        catch (Exception exception) when (AgentRpcInvocation.IsUnavailableFailure(
                                               exception,
                                               CancellationToken.None))
        {
            return new AgentBehaviorLoopSelection(defaultBehaviorLoop);
        }

        var selected = loops.FirstOrDefault(loop =>
            string.Equals(
                loop.Metadata.LoopId,
                requestedLoopId,
                StringComparison.OrdinalIgnoreCase)
            && (requestedSourceId is null
                || string.Equals(
                    loop.Metadata.SourceId,
                    requestedSourceId,
                    StringComparison.OrdinalIgnoreCase)));
        if (selected is not null && selected.Reference.TryAcquire(out var lease))
        {
            return new AgentBehaviorLoopSelection(selected.Metadata, selected.PackageId, lease);
        }

        return new AgentBehaviorLoopSelection(defaultBehaviorLoop);
    }
}

public sealed class AgentBehaviorLoopSelection : IDisposable
{
    private readonly IAgentBehaviorLoop? _localService;
    private AgentRpcLease<IAgentBehaviorLoop>? _lease;
    private int _disposed;

    internal AgentBehaviorLoopSelection(DefaultAgentBehaviorLoop defaultBehaviorLoop)
    {
        Descriptor = defaultBehaviorLoop.Descriptor;
        OwnerPackageId = DefaultAgentBehaviorLoop.BuiltInSourceId;
        RetirementToken = CancellationToken.None;
        _localService = defaultBehaviorLoop;
    }

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
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        if (_localService is not null)
        {
            return await _localService
                .RunAsync(context, runtime, cancellationToken).ConfigureAwait(false);
        }

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
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            Interlocked.Exchange(ref _lease, null)?.Dispose();
        }
    }
}
