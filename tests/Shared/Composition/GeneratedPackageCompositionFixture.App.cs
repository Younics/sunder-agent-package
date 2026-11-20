using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.TestSupport;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class GeneratedPackageCompositionFixture
{
    internal static ServiceProvider CreateGeneratedApp(Assembly app, IPackageContext context, ISunderRpcClient rpc,
        IBackgroundTaskQueue? background = null, IPackageWorkerExecutionObservations? executionObservations = null)
    {
        Assert.DoesNotContain(app.GetTypes(), type => type.Name == "AppPackageModule");
        var module = (ISunderAppPackageModule)Activator.CreateInstance(app.GetType("Sunder.Generated.Package.AppModule", true)!)!;
        var services = new ServiceCollection();
        module.ConfigureAppServices(services, context);
        Assert.DoesNotContain(services, service => service.ServiceType == typeof(IPackageContext) || service.ServiceType == typeof(ISunderRpcClient)
            || service.ServiceType == typeof(IPackageNotificationService) || service.ServiceType == typeof(IBackgroundTaskQueue)
            || service.ServiceType == typeof(IPackageSettingsNavigationService) || service.ServiceType == typeof(IPackageShellViewService)
            || service.ServiceType == typeof(IPackageDevelopmentService) || service.ServiceType == typeof(IPackageLogNavigationService)
            || service.ServiceType == typeof(IPackageAppStateUpdates) || service.ServiceType == typeof(IPackageWorkerExecutionObservations));
        services.AddSingleton(context); services.AddSingleton(rpc);
        services.AddSingleton<IPackageNotificationService>(NullPackageNotificationService.Instance);
        services.AddSingleton<IPackageSettingsNavigationService>(NullPackageSettingsNavigationService.Instance);
        services.AddSingleton<IPackageShellViewService>(new CapturingHistoryShellViewService());
        services.AddSingleton<IPackageDevelopmentService>(new CompositionDevelopmentHost());
        services.AddSingleton<IPackageLogNavigationService>(new CompositionDevelopmentHost());
        services.AddSingleton<IPackageAppStateUpdates, BuilderUiStateUpdates>();
        if (executionObservations is not null) services.AddSingleton(executionObservations);
        if (background is not null) services.AddSingleton(background);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
    }
}
