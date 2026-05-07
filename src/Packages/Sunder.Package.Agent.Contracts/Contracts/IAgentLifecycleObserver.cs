using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Contracts.Contracts;

public interface IAgentLifecycleObserver
{
    string ObserverId { get; }

    string DisplayName { get; }

    ValueTask<AgentLifecycleObserverResult?> HandleLifecycleEventAsync(
        AgentLifecycleEvent lifecycleEvent,
        CancellationToken cancellationToken = default);
}
