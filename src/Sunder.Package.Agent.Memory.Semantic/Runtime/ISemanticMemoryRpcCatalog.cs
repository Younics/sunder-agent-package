using System.Diagnostics.CodeAnalysis;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Memory.Semantic.Runtime;

public interface ISemanticMemoryRpcCatalog
{
    event Action? Changed;
    IReadOnlyList<SemanticProviderReference<IAgentRuntimeCatalog>> GetRuntimeCatalogs();
    IReadOnlyList<SemanticProviderReference<IAgentEmbeddingProvider>> GetEmbeddingProviders();
}

public abstract class SemanticProviderReference<T> where T : class
{
    public abstract SunderRpcProviderSnapshot Provider { get; }
    public SunderRpcEndpointReference Endpoint => Provider.Endpoint;
    public abstract bool TryAcquire([NotNullWhen(true)] out SemanticProviderLease<T>? lease);
}

public sealed class SemanticProviderLease<T>(string packageId, T service, CancellationToken retirementToken, Action? release = null) : IDisposable where T : class
{
    private int _disposed;
    public string PackageId => Volatile.Read(ref _disposed) == 0 ? packageId : throw new ObjectDisposedException(nameof(SemanticProviderLease<T>));
    public T Service => Volatile.Read(ref _disposed) == 0 ? service : throw new ObjectDisposedException(nameof(SemanticProviderLease<T>));
    public CancellationToken RetirementToken => Volatile.Read(ref _disposed) == 0 ? retirementToken : throw new ObjectDisposedException(nameof(SemanticProviderLease<T>));
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0) release?.Invoke();
    }
}
