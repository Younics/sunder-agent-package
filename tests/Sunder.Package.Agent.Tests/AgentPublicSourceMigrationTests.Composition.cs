using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    internal static async Task<(IAsyncDisposable Lifetime, IServiceProvider Services)> CreateCanonicalRuntime(
        Assembly assembly, IPackageContext context, RegressionTestExtensionCatalog catalog)
    {
        var generated = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(assembly, context, catalog.RpcForCaller("sunder.package.agent"));
        try
        {
            await catalog.RetireProviderAsync(catalog.RunControlProvider);
            // These scenarios exercise Agent's public/owner RPCs. Stack providers
            // are covered by the separate platform-import/composition scenarios.
            foreach (var provider in generated.Options.Providers.Where(provider => provider.ContractId.StartsWith("sunder.agent.", StringComparison.Ordinal)))
                catalog.AddGeneratedProvider("sunder.package.agent", provider.Handler, provider);
            await generated.Services.GetRequiredService<AgentBackgroundWorkService>().StartAsync();
            return (generated.Lifetime, generated.Services);
        }
        catch { await generated.Lifetime.DisposeAsync(); throw; }
    }
}
