using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// The controlled collaborators are test objects; the provider adapters and their
// generated dispatchers are the actual canonical Worker implementations. Never
// fall back to an old primary DLL when a generated role has not been built.
internal sealed class RegressionTestProviderRoles : IDisposable
{
    private readonly Dictionary<string, (RoleLoadContext Context, Assembly Assembly)> _roles = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private bool _disposed;

    internal SunderWorkerProviderRegistration Create(GeneratedServiceTestScope receiver, string contractId,
        string family, string provider, params object?[] arguments)
        => receiver.Register(CreateObject(family, provider, arguments), contractId);

    internal object CreateObject(string family, string typeName, params object?[] arguments)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return CreateObjectCore(family, typeName, arguments);
        }
    }

    private object CreateObjectCore(string family, string typeName, object?[] arguments)
    {
        var name = family == "Agent" ? "Sunder.Package.Agent" : "Sunder.Package.Agent." + family;
        var existing = arguments.Where(value => value is not null).Select(value => AgentRoleBoundary.Unwrap(value!).GetType().Assembly)
            .FirstOrDefault(assembly => assembly.GetName().Name == name
                && assembly.EntryPoint?.DeclaringType?.FullName == "Sunder.Generated.Package.WorkerEntryPoint");
        if (existing is not null)
            return AgentRoleBoundary.Create(existing.GetType(name + "." + typeName, throwOnError: true)!, arguments);
        if (!_roles.TryGetValue(family, out var role))
        {
            var configuration = typeof(RegressionTestProviderRoles).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()?.Configuration
                ?? throw new InvalidOperationException("The test assembly has no build configuration metadata.");
            var path = Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, "src", name, "bin", configuration,
                "net10.0/sunder-roles/worker", RuntimeInformation.RuntimeIdentifier, name + ".dll");
            Assert.True(File.Exists(path), $"Build the canonical {family} Worker role before running this provider scenario: {path}");
            var context = new RoleLoadContext(path);
            try
            {
                var assembly = context.LoadFromAssemblyPath(path);
                Assert.Equal("Sunder.Generated.Package.WorkerEntryPoint", assembly.EntryPoint?.DeclaringType?.FullName);
                Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
                    reference.Name is "Sunder.Package.Agent.Protocol" or "Sunder.Package.Agent.OwnerProtocol"
                    || reference.Name?.EndsWith(".Protocol", StringComparison.Ordinal) == true);
                role = (context, assembly);
                _roles.Add(family, role);
            }
            catch { context.Unload(); throw; }
        }
        return AgentRoleBoundary.Create(role.Assembly.GetType(name + "." + typeName, throwOnError: true)!, arguments);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var role in _roles.Values) role.Context.Unload();
            _roles.Clear();
        }
    }

    private sealed class RoleLoadContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal) == true
                || name.Name == "Sunder.Agent.Execution.Common")
                throw new InvalidDataException($"A canonical provider unexpectedly references a cross-role Agent assembly: {name}.");
            // SDK/BCL/framework identity is shared with the loopback Host. Package
            // domain interfaces/models are defined in the collectible role itself.
            var shared = Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name));
            if (shared is not null) return shared;
            return _resolver.ResolveAssemblyToPath(name) is { } dependency ? LoadFromAssemblyPath(dependency) : null;
        }

        protected override nint LoadUnmanagedDll(string name)
            => _resolver.ResolveUnmanagedDllToPath(name) is { } dependency ? LoadUnmanagedDllFromPath(dependency) : 0;
    }
}
