using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Memory.Semantic;

public sealed partial class MemoryLocalStore : ISunderWorkerActivationService
{
    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]
    internal MemoryLocalStore(IPackageContext packageContext, bool deferInitialization = true)
        : this(packageContext, SqliteMemoryPhysicalMaintenance.Instance, initialize: !deferInitialization) { }

    public ValueTask ActivateAsync(SunderWorkerContext context, CancellationToken cancellationToken = default)
    {
        Initialize(cancellationToken);
        return ValueTask.CompletedTask;
    }
}
