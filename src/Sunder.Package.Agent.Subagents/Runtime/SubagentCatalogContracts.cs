using Sunder.Package.Agent.Contracts.Models;

namespace Sunder.Package.Agent.Subagents.Runtime;

[Flags]
public enum SubagentCatalogChange
{
    None = 0,
    RuntimeCatalog = 1,
    ChatProviders = 2,
    Tools = 4,
    SelectableCapabilities = 8,
    BehaviorLoops = 16,
}

public interface ISubagentChatCatalog
{
    AgentProviderDescriptor Descriptor { get; }
    ValueTask<IReadOnlyList<AgentModelDescriptor>> GetAvailableModelsAsync(CancellationToken cancellationToken = default);
    ValueTask<AgentProviderReadiness> GetReadinessAsync(CancellationToken cancellationToken = default);
}

public interface ISubagentToolCatalog
{
    string SourceId { get; }
    string DisplayName { get; }
    ValueTask<IReadOnlyList<AgentToolDescriptor>> ListToolsAsync(AgentToolSourceContext context, CancellationToken cancellationToken = default);
}

public interface ISubagentBehaviorCatalog
{
    AgentBehaviorLoopDescriptor Descriptor { get; }
}
