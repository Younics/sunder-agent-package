using System.Reflection;
using System.Runtime.ExceptionServices;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Services;
using Sunder.Sdk.Worker;

namespace Sunder.Package.Agent.Tests;

// The same receiver-scoped SDK owner is supplied to generated catalogs, clients
// and provider factories. This fixture never creates a replacement RPC adapter.
internal sealed class GeneratedServiceTestScope : IServiceProvider, IAsyncDisposable
{
    private readonly ServiceProvider _services;
    private readonly object _gate = new();
    private readonly Dictionary<Type, object> _generated = [];
    private bool _disposed;
    private Task? _dispose;

    internal GeneratedServiceTestScope(ISunderRpcClient client)
    {
        Client = client;
        _services = new ServiceCollection()
            .AddSingleton(client)
            .AddSingleton<SunderRpcServiceBindingLifetime>()
            .BuildServiceProvider();
    }

    internal ISunderRpcClient Client { get; }

    public object? GetService(Type serviceType)
    {
        if (serviceType == typeof(IServiceProvider)) return this;
        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(IServiceProviders<>))
            return Catalog(serviceType);
        if (serviceType.IsGenericType && serviceType.GetGenericTypeDefinition() == typeof(Func<,,>)
            && serviceType.GenericTypeArguments[0] == typeof(SunderRpcProviderSnapshot)
            && serviceType.GenericTypeArguments[1] == typeof(CancellationToken))
            return ReferenceFactory(serviceType);
        return _services.GetService(serviceType);
    }

    internal IServiceProviders<TService> Providers<TService>() where TService : class
        => (IServiceProviders<TService>)Catalog(typeof(IServiceProviders<TService>));

    internal object Providers(Type serviceType)
        => Catalog(typeof(IServiceProviders<>).MakeGenericType(serviceType));

    // A generated fixture can receive a projected interface from a different CLR
    // assembly. Rebind only that already-received exact locator, through the SDK
    // and this receiver's generated factory. Never copy a service object, invent a
    // locator, or obtain authority from a replacement activation.
    internal ValueTask<object> RebindReferenceAsync(object reference, Type targetService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(targetService);
        var type = reference.GetType();
        if (!type.IsGenericType || type.GetGenericTypeDefinition() != typeof(ServiceReference<>))
            throw new ArgumentException("A received SDK service reference is required.", nameof(reference));
        return (ValueTask<object>)typeof(GeneratedServiceTestScope)
            .GetMethod(nameof(RebindReferenceCoreAsync), BindingFlags.Instance | BindingFlags.NonPublic)!
            .MakeGenericMethod(targetService).Invoke(this, [reference, cancellationToken])!;
    }

    private async ValueTask<object> RebindReferenceCoreAsync<TService>(object reference,
        CancellationToken cancellationToken) where TService : class
    {
        var type = reference.GetType();
        var binding = type.GetProperty("Binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(reference)
            ?? throw new InvalidOperationException("An unbound service reference cannot cross a fixture boundary.");
        var owner = binding.GetType().GetProperty("Owner", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(binding);
        var lifetime = _services.GetRequiredService<SunderRpcServiceBindingLifetime>();
        if (!ReferenceEquals(owner, lifetime))
            throw new InvalidOperationException("The reference must already belong to this generated receiver scope.");
        await ((ValueTask)type.GetMethod(nameof(ServiceReference<TService>.EnsureCurrentAsync))!
            .Invoke(reference, [cancellationToken])!).ConfigureAwait(false);
        // The target's generated catalog enforces its selected descriptor/service
        // identity. Matching the SDK binding object cannot follow a replacement,
        // even if it has the same package/provider ID or endpoint spelling.
        var snapshot = await Providers<TService>().ListAsync(cancellationToken).ConfigureAwait(false);
        var entry = snapshot.Providers.SingleOrDefault(provider => ReferenceEquals(binding,
            provider.Reference.GetType().GetProperty("Binding", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(provider.Reference)));
        if (entry is null)
        {
            await ((ValueTask)type.GetMethod(nameof(ServiceReference<TService>.EnsureCurrentAsync))!
                .Invoke(reference, [cancellationToken])!).ConfigureAwait(false);
            throw new InvalidOperationException("The exact received reference is not visible through the requested generated service catalog.");
        }
        await entry.EnsureCurrentAsync(cancellationToken).ConfigureAwait(false);
        return entry.Reference;
    }

    private object ReferenceFactory(Type factoryType)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_generated.TryGetValue(factoryType, out var existing)) return existing;
            var catalog = Providers(factoryType.GenericTypeArguments[2]);
            // This is the same generated BindExact delegate installed by the
            // canonical composition. The SDK still owns identity and retirement.
            var bind = catalog.GetType().GetMethod("BindExact", BindingFlags.Instance | BindingFlags.NonPublic,
                [typeof(SunderRpcProviderSnapshot), typeof(CancellationToken)])
                ?? throw new InvalidOperationException($"'{catalog.GetType()}' has no generated reference factory.");
            var factory = bind.CreateDelegate(factoryType, catalog);
            _generated.Add(factoryType, factory);
            return factory;
        }
    }

    private object Catalog(Type catalogInterface)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_generated.TryGetValue(catalogInterface, out var existing)) return existing;
            var service = catalogInterface.GenericTypeArguments[0];
            var implementation = service.Assembly.GetTypes().Single(type => type.IsClass && !type.IsAbstract
                && !type.ContainsGenericParameters && catalogInterface.IsAssignableFrom(type)
                && type.GetConstructor([typeof(ISunderRpcClient), typeof(IServiceProvider)]) is not null);
            var result = Activator.CreateInstance(implementation, Client, this)!;
            _generated.Add(catalogInterface, result);
            return result;
        }
    }

    internal SunderWorkerProviderRegistration Register(object implementation, string contractId)
    {
        implementation = AgentRoleBoundary.Unwrap(implementation);
        var method = implementation.GetType().GetMethod("CreateSunderRpcProviderRegistrationsForServices",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
            [typeof(ISunderRpcClient), typeof(IServiceProvider)]);
        try
        {
            if (method is not null)
                return ((IReadOnlyList<SunderWorkerProviderRegistration>)method.Invoke(implementation, [Client, this])!)
                    .Single(registration => registration.ContractId == contractId);
            method = implementation.GetType().GetMethod("CreateSunderRpcProviderRegistrationForServices",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                [typeof(ISunderRpcClient), typeof(IServiceProvider)])
                ?? throw new InvalidOperationException($"'{implementation.GetType()}' must supply its actual generated provider registration.");
            var registration = (SunderWorkerProviderRegistration)method.Invoke(implementation, [Client, this])!;
            if (registration.ContractId != contractId)
                throw new InvalidOperationException($"The generated registration implements '{registration.ContractId}', not '{contractId}'.");
            return registration;
        }
        catch (TargetInvocationException error) when (error.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(error.InnerException).Throw();
            throw;
        }
    }

    public ValueTask DisposeAsync()
    {
        object[] generated;
        TaskCompletionSource completion;
        lock (_gate)
        {
            if (_dispose is not null) return new(_dispose);
            _disposed = true;
            completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
            _dispose = completion.Task;
            generated = _generated.Values.ToArray();
            _generated.Clear();
        }
        _ = DrainAsync(generated, completion);
        return new(completion.Task);
    }

    private async Task DrainAsync(object[] generated, TaskCompletionSource completion)
    {
        try
        {
            try
            {
                await Task.WhenAll(generated.OfType<IAsyncDisposable>()
                    .Select(async value => await value.DisposeAsync().ConfigureAwait(false))).ConfigureAwait(false);
            }
            finally { await _services.DisposeAsync().ConfigureAwait(false); }
            completion.TrySetResult();
        }
        catch (Exception error) { completion.TrySetException(error); }
    }
}
