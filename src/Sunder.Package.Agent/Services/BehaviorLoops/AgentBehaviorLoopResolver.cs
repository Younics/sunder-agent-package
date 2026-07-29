using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

public sealed class AgentBehaviorLoopResolver(
    IPackageExtensionCatalog extensionCatalog,
    DefaultAgentBehaviorLoop defaultBehaviorLoop)
{
    private readonly IPackageExtensionInvocationCatalog _invocationCatalog =
        AgentExtensionInvocation.Require(extensionCatalog);
    private readonly DefaultAgentBehaviorLoop _defaultBehaviorLoop = defaultBehaviorLoop;

    public AgentBehaviorLoopSelection Resolve(AgentProfileRecord profile)
    {
        var requestedLoopId = string.IsNullOrWhiteSpace(profile.BehaviorLoopId)
            ? DefaultAgentBehaviorLoop.LoopId
            : profile.BehaviorLoopId.Trim();
        var requestedSourceId = string.IsNullOrWhiteSpace(profile.BehaviorLoopSourceId)
            ? null
            : profile.BehaviorLoopSourceId.Trim();

        var loops = AgentExtensionInvocation.Snapshot(
            _invocationCatalog,
            PackageExtensionPoints.BehaviorLoops,
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

        return new AgentBehaviorLoopSelection(_defaultBehaviorLoop);
    }
}

public sealed class AgentBehaviorLoopSelection : IDisposable
{
    private readonly IAgentBehaviorLoop? _localLoop;
    private IPackageExtensionLease<IAgentBehaviorLoop>? _lease;

    internal AgentBehaviorLoopSelection(
        AgentBehaviorLoopDescriptor descriptor,
        string ownerPackageId,
        IPackageExtensionLease<IAgentBehaviorLoop> lease)
    {
        Descriptor = descriptor;
        OwnerPackageId = ownerPackageId;
        _lease = lease;
        RetirementToken = lease.RetirementToken;
    }

    internal AgentBehaviorLoopSelection(IAgentBehaviorLoop localLoop)
    {
        _localLoop = localLoop;
        Descriptor = localLoop.Descriptor with
        {
            FeatureKinds = localLoop.Descriptor.FeatureKinds?.ToArray(),
        };
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
            return await (_localLoop
                          ?? throw new ObjectDisposedException(nameof(AgentBehaviorLoopSelection)))
                .RunAsync(context, runtime, cancellationToken).ConfigureAwait(false);
        }

        using var invocation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            RetirementToken);
        try
        {
            var result = await lease.Contribution
                .RunAsync(context, runtime, invocation.Token).ConfigureAwait(false);
            if (IsRetiring && !cancellationToken.IsCancellationRequested)
            {
                throw AgentExtensionInvocation.Unavailable(OwnerPackageId!);
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
            throw AgentExtensionInvocation.Unavailable(OwnerPackageId!, exception);
        }
        catch (Exception exception) when (
            IsRetiring
            && !cancellationToken.IsCancellationRequested)
        {
            throw AgentExtensionInvocation.Unavailable(OwnerPackageId!, exception);
        }
    }

    public void Dispose()
        => Interlocked.Exchange(ref _lease, null)?.Dispose();
}
