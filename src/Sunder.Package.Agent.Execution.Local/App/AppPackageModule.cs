using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Package.Agent.Shared.Presentation;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;

namespace Sunder.Package.Agent.Execution.Local;

public sealed class AppPackageModule : ISunderAppPackageModule
{
    public void ConfigureAppServices(IServiceCollection services, IPackageContext context)
    {
        services.TryAddSingleton<ILocalExecutionPresentationClient>(provider =>
            new LocalExecutionPresentationRpcClient(
                provider.GetRequiredService<ISunderRpcClient>()));
        services.AddTransient(provider => new LocalExecutionSettingsViewModel(
            provider.GetRequiredService<ILocalExecutionPresentationClient>()));
        services.AddTransient(provider => new LocalExecutionSettingsView(
            provider.GetRequiredService<LocalExecutionSettingsViewModel>(),
            context,
            provider.GetRequiredService<IPackageNotificationService>()));
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterSettingsView<LocalExecutionSettingsView>();
}
