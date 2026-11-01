using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

// Read-only fixture over ordinary package outputs. There is no source overlay,
// secondary implementation project, automatic Accept, or legacy DLL fallback.
internal sealed class CanonicalAgentRoles : IDisposable
{
    private readonly RoleContext _worker;
    private readonly RoleContext _app;
    internal static string Configuration => typeof(CanonicalAgentRoles).Assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;
    internal static string SourceRoot => Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, "src/Sunder.Package.Agent");
    internal static string AnalysisRoot => Path.Combine(SourceRoot, "obj", Configuration, "net10.0/sunder-analysis", RuntimeInformation.RuntimeIdentifier, "sunder-rpc");
    internal static string Output(string role) => Path.Combine(SourceRoot, "bin", Configuration, "net10.0/sunder-roles", role, RuntimeInformation.RuntimeIdentifier);

    internal CanonicalAgentRoles()
    {
        var workerPath = Path.Combine(Output("worker"), "Sunder.Package.Agent.dll");
        var appPath = Path.Combine(Output("app"), "Sunder.Package.Agent.App.dll");
        Assert.True(File.Exists(workerPath) && File.Exists(appPath), "Build the canonical Agent Worker and App roles before this required-input lane.");
        _worker = new(workerPath);
        _app = new(appPath);
        try
        {
            Worker = _worker.LoadFromAssemblyPath(workerPath);
            App = _app.LoadFromAssemblyPath(appPath);
            Assert.Equal("Sunder.Generated.Package.WorkerEntryPoint", Worker.EntryPoint?.DeclaringType?.FullName);
            Assert.NotNull(App.GetType("Sunder.Generated.Package.AppModule"));
            AgentCanonicalSourceMigrationTests.AssertRoleLocalOwnerModels(Worker);
            AgentAppSourceMigrationTests.AssertRoleLocalModels(App);
        }
        catch { Dispose(); throw; }
    }

    internal Assembly Worker { get; }
    internal Assembly App { get; }
    internal static JsonDocument Receipt(bool app = false) => JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AnalysisRoot,
        app ? "app-authoring-receipt.v1.json" : "authoring-receipt.v1.json")));
    public void Dispose() { _app.Unload(); _worker.Unload(); }

    private sealed class RoleContext(string path) : AssemblyLoadContext(isCollectible: true)
    {
        private readonly AssemblyDependencyResolver _resolver = new(path);
        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name?.StartsWith("Sunder.Package.Agent", StringComparison.Ordinal) == true)
                throw new InvalidDataException("An isolated canonical role references another Agent binary: " + name);
            var shared = Default.Assemblies.FirstOrDefault(assembly => AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), name));
            return shared ?? (_resolver.ResolveAssemblyToPath(name) is { } dependency ? LoadFromAssemblyPath(dependency) : null);
        }
        protected override nint LoadUnmanagedDll(string name)
            => _resolver.ResolveUnmanagedDllToPath(name) is { } dependency ? LoadUnmanagedDllFromPath(dependency) : 0;
    }
}
