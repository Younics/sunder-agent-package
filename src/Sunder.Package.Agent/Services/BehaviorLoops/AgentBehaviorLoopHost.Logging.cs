using Sunder.Package.Agent.Contracts.Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Services.BehaviorLoops;

internal sealed partial class AgentBehaviorLoopHost
{
    private static PackageLogLevel ToPackageLogLevel(AgentLogLevel level)
        => level switch
        {
            AgentLogLevel.Trace => PackageLogLevel.Trace,
            AgentLogLevel.Debug => PackageLogLevel.Debug,
            AgentLogLevel.Information => PackageLogLevel.Information,
            AgentLogLevel.Warning => PackageLogLevel.Warning,
            AgentLogLevel.Error => PackageLogLevel.Error,
            AgentLogLevel.Critical => PackageLogLevel.Critical,
            _ => PackageLogLevel.Information,
        };

    private sealed class ProviderEventSink(IPackageEventLogger eventLogger) : IAgentProviderEventSink
    {
        public ValueTask WriteAsync(
            AgentLogLevel level,
            string eventName,
            string message,
            IReadOnlyDictionary<string, object?>? attributes = null,
            Exception? exception = null,
            CancellationToken cancellationToken = default)
            => eventLogger.WriteAsync(
                ToPackageLogLevel(level),
                eventName,
                message,
                attributes,
                exception,
                cancellationToken);
    }
}
