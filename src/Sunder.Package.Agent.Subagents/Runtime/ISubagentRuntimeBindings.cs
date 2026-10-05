using System.Diagnostics.CodeAnalysis;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Subagents.Runtime;

public interface ISubagentRuntimeBindings : IAsyncDisposable
{
    event Action? RuntimeCatalogChanged;
    event Action<SubagentCatalogChange>? CatalogChanged;
    ValueTask StartAsync(CancellationToken cancellationToken = default);
    IReadOnlyList<SubagentProviderReference<IAgentRuntimeCatalog>> GetRuntimeCatalogs();
    IReadOnlyList<SubagentProviderReference<IAgentChildRunExecutor>> GetChildRunExecutors();
    IReadOnlyList<SubagentProviderReference<ISubagentChatCatalog>> GetChatProviders();
    IReadOnlyList<SubagentProviderReference<ISubagentToolCatalog>> GetToolSources();
    IReadOnlyList<SubagentProviderReference<IAgentProfileSelectableCapabilityProvider>> GetSelectableCapabilityProviders();
    IReadOnlyList<SubagentProviderReference<ISubagentBehaviorCatalog>> GetBehaviorLoops();
}

public abstract class SubagentProviderReference<T> where T : class
{
    public abstract SunderRpcProviderSnapshot Provider { get; }
    public abstract bool TryAcquire([NotNullWhen(true)] out SubagentProviderLease<T>? lease);
}

public sealed class SubagentProviderLease<T>(string packageId, T service, CancellationToken retirementToken, Action? release = null) : IDisposable where T : class
{
    private int _disposed;
    public string PackageId => Volatile.Read(ref _disposed) == 0 ? packageId : throw new ObjectDisposedException(nameof(SubagentProviderLease<T>));
    public T Service => Volatile.Read(ref _disposed) == 0 ? service : throw new ObjectDisposedException(nameof(SubagentProviderLease<T>));
    public CancellationToken RetirementToken => Volatile.Read(ref _disposed) == 0 ? retirementToken : throw new ObjectDisposedException(nameof(SubagentProviderLease<T>));
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) release?.Invoke(); }
}
