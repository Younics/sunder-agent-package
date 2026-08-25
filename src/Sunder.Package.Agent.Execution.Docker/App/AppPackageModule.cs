using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
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
        services.AddTransient(provider => new DockerExecutionSettingsView(
            provider.GetRequiredService<DockerExecutionSettingsViewModel>(),
            context,
            provider.GetRequiredService<IPackageNotificationService>()));
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterSettingsView<DockerExecutionSettingsView>();
}
