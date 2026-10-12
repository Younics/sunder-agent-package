using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    internal void AddRuntimeCatalog(object implementation)
        => AddProvider(RegressionTestBindings.RuntimeCatalog, implementation, "sunder.package.agent");

    internal void AddChildRunExecutor(object implementation, string packageId = "sunder.package.agent")
        => AddProvider(RegressionTestBindings.ChildRunExecutor, implementation, packageId);

    internal void AddGeneratedProvider(string packageId, object identity, SunderWorkerProviderRegistration registration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _catalogDisposed) != 0, this);
        _client.State.AddGenerated(packageId, identity, registration, EmptyProviderLifetime.Instance, registration.ProviderId);
    }

    internal async ValueTask<ServiceProvider<TService>> AddAndSelectProviderAsync<TService>(
        string contractId, IServiceProviders<TService> providers, object implementation, string packageId,
        CancellationToken cancellationToken = default) where TService : class
    {
        cancellationToken.ThrowIfCancellationRequested();
        var registration = AddProvider(contractId, implementation, packageId);
        var catalog = await providers.ListAsync(cancellationToken).ConfigureAwait(false);
        return catalog.Providers.Single(provider => provider.ActivationId == registration.ActivationId
            && provider.ProviderId == registration.ProviderId);
    }

    public void AddTool(object tool, string packageId = "test.package")
    {
        var domainTool = AgentRoleBoundary.As<IAgentTool>(tool);
        AddProvider(RegressionTestBindings.ToolSources, new RegressionTestSingleToolSource(domainTool), packageId, tool,
            "fixture.tool." + domainTool.Descriptor.ToolId.Replace('_', '.').Replace('-', '.').ToLowerInvariant());
    }

    public void AddBehaviorLoop(object implementation, string packageId = "test.package")
        => AddProvider(RegressionTestBindings.BehaviorLoops, implementation, packageId);

    internal SunderRpcProviderSnapshot AddProvider(string contractId, object implementation,
        string packageId = "test.package", object? registrationIdentity = null, string? providerId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _catalogDisposed) != 0, this);
        var receiver = Receiver(packageId);
        var registration = _providers.Create(contractId, implementation, receiver);
        return _client.State.AddGenerated(packageId, registrationIdentity ?? implementation, registration,
            EmptyProviderLifetime.Instance, providerId ?? registration.ProviderId);
    }

    private sealed class EmptyProviderLifetime : IDisposable
    {
        internal static EmptyProviderLifetime Instance { get; } = new();
        public void Dispose() { }
    }
}

internal sealed class RegressionTestSourceProviders : IDisposable
{
    internal SunderWorkerProviderRegistration Create(string contractId, object implementation, GeneratedServiceTestScope receiver)
    {
        implementation = AgentRoleBoundary.Unwrap(implementation);
        if (implementation.GetType().GetMethods(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
            .Any(method => method.Name is "CreateSunderRpcProviderRegistrationsForServices" or "CreateSunderRpcProviderRegistrationForServices"))
            return receiver.Register(implementation, contractId);
        if (RegressionTestGeneratedProviders.TryRegister(receiver, contractId, implementation, out var registration))
            return registration;

        throw new InvalidOperationException($"'{implementation.GetType()}' has no current generated provider registration. "
            + "Use its generated test-module provider for RPC integration, or inject a natural service fake for a domain-only case.");
    }

    public void Dispose() { }
}
