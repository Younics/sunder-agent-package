using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Avalonia;
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
    }

    public void RegisterAppContributions(
        ISunderAppContributionRegistry registry,
        IServiceProvider services)
        => registry.RegisterSettingsView<LocalExecutionSettingsView>();
}
