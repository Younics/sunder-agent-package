using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;

namespace Sunder.Agent.Extension.Minimal;

public sealed class PackageModule : ISunderRuntimePackageModule
{
    public void ConfigureRuntimeServices(IServiceCollection services, IPackageContext context)
    {
        services.AddSingleton<CurrentUtcTimeTool>();
        services.AddSingleton(provider => new AgentStaticToolSourceAdapter(
            "minimal-tools",
            "Minimal tools",
            "sample",
            [provider.GetRequiredService<CurrentUtcTimeTool>()]));
    }

    public void RegisterRuntimeContributions(
        ISunderRuntimeContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterRpcProvider(
            "minimal.tools",
            AgentToolSourceRpc.CreateHandler(services.GetRequiredService<AgentStaticToolSourceAdapter>()));
}
