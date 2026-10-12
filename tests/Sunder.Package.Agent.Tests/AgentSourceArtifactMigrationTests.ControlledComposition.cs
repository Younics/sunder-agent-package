using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    // Controlled HTTP/process collaborators need a pre-construction DI override.
    // Compile the candidate's generated graph against its own role DLL, adding only
    // that test hook and a separate namespace. Never reconstruct the graph from
    // retired wrappers or compile captured historical production source.
    private static void WriteControlledWorkerEntry(string worker, string directory)
    {
        var path = Path.Combine(CanonicalPackageRoles.Analysis(worker), "roles/worker/worker-entry.g.cs");
        var source = File.ReadAllText(path);
        const string entry = "internal static class WorkerEntryPoint";
        const string build = "var provider = services.BuildServiceProvider(";
        Assert.Equal(1, source.Split(entry, StringSplitOptions.None).Length - 1);
        Assert.Equal(1, source.Split(build, StringSplitOptions.None).Length - 1);
        var projected = source.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace("namespace Sunder.Generated.Package\n", "namespace Sunder.Tests.Generated.Package\n", StringComparison.Ordinal)
            .Replace("namespace Sunder.Generated.Package;", "namespace Sunder.Tests.Generated.Package;", StringComparison.Ordinal)
            .Replace("namespace Sunder.Generated.Package {", "namespace Sunder.Tests.Generated.Package {", StringComparison.Ordinal)
            .Replace(entry + "\n{", entry + "\n{\n    internal static global::System.Action<global::Microsoft.Extensions.DependencyInjection.IServiceCollection>? Configure { get; set; }", StringComparison.Ordinal)
            .Replace(build, "Configure?.Invoke(services);\n        " + build, StringComparison.Ordinal);
        projected = System.Text.RegularExpressions.Regex.Replace(projected,
            @"namespace Sunder\.Generated\.Packages\.([A-Za-z0-9_]+_Internal)\b",
            "namespace Sunder.Tests.Generated.Packages.$1");
        projected = System.Text.RegularExpressions.Regex.Replace(projected,
            @"global::Sunder\.Generated\.Packages\.([A-Za-z0-9_]+_Internal)\.([A-Za-z0-9_]+LocalService)\b",
            "global::Sunder.Tests.Generated.Packages.$1.$2");
        Assert.Contains("Configure { get; set; }", projected, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(directory, "ControlledWorkerEntry.g.cs"), projected);
        Assert.Equal(source, File.ReadAllText(path));
    }

    private static (SunderWorkerOptions Options, IAsyncDisposable Lifetime, IServiceProvider Services) CreateControlledGeneratedWorker(
        Assembly probe, IPackageContext context, ISunderRpcClient rpc, Action<IServiceCollection> configure)
    {
        var entry = probe.GetType("Sunder.Tests.Generated.Package.WorkerEntryPoint", true)!;
        var hook = entry.GetProperty("Configure", BindingFlags.Static | BindingFlags.NonPublic)!;
        hook.SetValue(null, configure);
        try
        {
            var options = (SunderWorkerOptions)entry.GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic,
                [typeof(IPackageContext), typeof(ISunderRpcClient)])!.Invoke(null, [context, rpc])!;
            return GetGeneratedComposition(options);
        }
        finally { hook.SetValue(null, null); }
    }

    internal static async Task ActivateGeneratedComposition(SunderWorkerOptions options)
    {
        var context = (SunderWorkerContext)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(SunderWorkerContext));
        if (options.Composition is { } composition)
        {
            await Stage("PrepareCandidateAsync", CancellationToken.None);
            var generation = Activator.CreateInstance(typeof(SunderWorkerGenerationContext), BindingFlags.Instance | BindingFlags.NonPublic,
                binder: null, args: [Guid.NewGuid(), 1L], culture: null)!;
            await Stage("CommitGenerationAsync", generation, CancellationToken.None);
            await Stage("ActivateAsync", context, CancellationToken.None);

            Task Stage(string name, params object[] arguments)
                => (Task)typeof(SunderWorkerComposition).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(composition, arguments)!;
        }
        if (options.OnActivated is { } activate) await activate(context, CancellationToken.None);
    }

    private static object ProviderCredentials(Assembly worker, IServiceProvider services, string family)
    {
        var settings = services.GetRequiredService(worker.GetType($"Sunder.Package.Agent.Provider.{family}.ProviderSettings", true)!);
        return settings.GetType().GetProperty("Credentials", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(settings)!;
    }

    private static void ConfigureProviderFactory(Assembly worker, IServiceCollection services, IPackageContext context, string family, object factory)
    {
        foreach (var suffix in family == "Gemini" ? new[] { "AgentProvider", "EmbeddingProvider" } : ["AgentProvider"])
        {
            var type = worker.GetType($"Sunder.Package.Agent.Provider.{family}.{family}{suffix}", true)!;
            services.AddSingleton(type, provider => AgentRoleBoundary.Create(type, context, ProviderCredentials(worker, provider, family), factory));
        }
    }
}
