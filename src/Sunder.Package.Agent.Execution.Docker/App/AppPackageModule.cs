using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Execution.Docker;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.TryAddSingleton<IDockerExecutionPresentationClient>(provider =>
            new DockerExecutionPresentationRpcClient(
                provider.GetRequiredService<ISunderRpcClient>()));
        services.AddTransient(provider => new DockerExecutionSettingsViewModel(
            provider.GetRequiredService<IDockerExecutionPresentationClient>(),
            provider.GetRequiredService<IBackgroundProcessQueue>(),
            context.Logging.LoggerFactory.CreateLogger<DockerExecutionSettingsViewModel>()));
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterSettingsView<DockerExecutionSettingsView>();
}
