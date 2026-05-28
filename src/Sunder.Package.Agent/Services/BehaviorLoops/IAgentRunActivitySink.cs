using Sunder.Package.Agent.Models;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal interface IAgentRunActivitySink
{
    void ReportRunActivity(AgentRunActivityKind kind, string text);
}
