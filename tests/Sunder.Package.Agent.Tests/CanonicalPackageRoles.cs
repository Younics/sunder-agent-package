using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Read-only canonical outputs. Historical source captures are never compiler inputs.
internal sealed class CanonicalPackageRoles : IDisposable
{
    private readonly List<RoleContext> _contexts = [];
    internal static string Configuration => typeof(CanonicalPackageRoles).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
    internal static string Source(string family) => Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, "src",
        family == "Agent" ? "Sunder.Package.Agent" : "Sunder.Package.Agent." + family);
    internal static string Analysis(string root) => Path.Combine(root, "obj", Configuration,
        "net10.0", "sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc");
    internal static string Output(string root, string role) => Path.Combine(root, "bin", Configuration,
        "net10.0", "sunder-roles", role, RuntimeInformation.RuntimeIdentifier);

    internal static byte[]? ReadOwnedBaseline(string root)
    {
        var path = Path.Combine(root, "sunder-contracts.owned.json");
        return File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    internal Assembly Load(string family, string role, string? root = null)
    {
        root ??= Source(family);
        var name = family == "Agent" ? "Sunder.Package.Agent" : "Sunder.Package.Agent." + family;
        var path = Path.Combine(Output(root, role), name + (role == "app" ? ".App" : "") + ".dll");
        Assert.True(File.Exists(path), $"Build the canonical {family} {role} before this required-input probe: {path}");
        var context = new RoleContext(path);
        _contexts.Add(context);
        var assembly = context.LoadFromAssemblyPath(path);
        Assert.DoesNotContain(assembly.GetReferencedAssemblies(), reference =>
            reference.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal)
            || reference.Name == "Sunder.Agent.Execution.Common");
        if (role == "worker") Assert.Equal("Sunder.Generated.Package.WorkerEntryPoint", assembly.EntryPoint?.DeclaringType?.FullName);
        else Assert.NotNull(assembly.GetType("Sunder.Generated.Package.AppModule"));
        return assembly;
    }

    internal static SunderRpcContractDescriptor OwnedDescriptor(string root, string contractId)
    {
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Analysis(root), "authoring-receipt.v1.json")));
        var emitted = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray(),
            entry => entry.GetProperty("contractId").GetString() == contractId);
        if (emitted.GetProperty("exposure").GetString() == "package")
        {
            var successor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(emitted.GetProperty("canonicalDescriptor").GetString()!));
            CurrentPrivateRpcDescriptors.AssertCurrent(successor);
            Assert.Equal(receipt.RootElement.GetProperty("packageVersion").GetString(), successor.Version);
            return successor;
        }
        using var owned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "sunder-contracts.owned.json")));
        var current = Assert.Single(owned.RootElement.GetProperty("contracts").EnumerateObject(),
            entry => entry.Value.GetProperty("contractId").GetString() == contractId).Value.GetProperty("current");
        Assert.Equal("release-frozen", current.GetProperty("state").GetString());
        var descriptor = SunderRpcContractDescriptor.Parse(Encoding.UTF8.GetBytes(current.GetProperty("canonicalDescriptor").GetString()!));
        var oracle = CurrentPublicRpcDescriptors.Get(contractId);
        Assert.Equal(oracle.GetCanonicalUtf8(), descriptor.GetCanonicalUtf8());
        Assert.Equal(oracle.GetCanonicalAbiProjectionUtf8(), descriptor.GetCanonicalAbiProjectionUtf8());
        Assert.Equal(oracle.Sha256, current.GetProperty("descriptorSha256").GetString());
        Assert.Equal(oracle.AbiSha256, current.GetProperty("abiSha256").GetString());
        return descriptor;
    }

    public void Dispose()
    {
        foreach (var context in _contexts) context.Unload();
    }

    private sealed class RoleContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name!.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal)
                || name.Name == "Sunder.Agent.Execution.Common")
                throw new InvalidDataException("A canonical role requested a cross-role binary: " + name);
            return Default.Assemblies.FirstOrDefault(value => AssemblyName.ReferenceMatchesDefinition(value.GetName(), name))
                ?? (_resolver.ResolveAssemblyToPath(name) is { } dependency ? LoadFromAssemblyPath(dependency) : null);
        }
        protected override nint LoadUnmanagedDll(string name)
            => _resolver.ResolveUnmanagedDllToPath(name) is { } dependency ? LoadUnmanagedDllFromPath(dependency) : 0;
    }
}
