using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Shared.PackageViews;
using Sunder.Package.Agent.Shared.Presentation;
using AgentServices = Sunder.Generated.Packages.Sunder_Package_Agent_Services;

namespace Sunder.Package.Agent.Services;

public sealed class AgentToolPresentationService
{
    private readonly TranscriptToolPresentationService _shared = new();
    private readonly AgentServices.IAgentRuntimeService? _owner;

    public AgentToolPresentationService() { }

    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal AgentToolPresentationService(AgentServices.IAgentRuntimeService owner)
        => _owner = owner;

    public async ValueTask<AgentToolPresentation> ResolveAsync(AgentTurnItemRecord item, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_owner is not null)
        {
            try
            {
                var request = TranscriptToolPresentationService.CreateRequest(item);
                var presentation = await _owner.GetToolPresentationAsync(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                if (presentation is not null) return _shared.Resolve(item, presentation);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception exception) when (!PresentationExceptionPolicy.IsFatal(exception))
            {
                // The locally derived transcript presentation remains available offline.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return _shared.Resolve(item);
    }
}
