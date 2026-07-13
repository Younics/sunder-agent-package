using Microsoft.Extensions.DependencyInjection;

namespace Sunder.Package.Agent.Shared.Composition;

internal static class PackageServiceCollectionExtensions
{
    public static IServiceCollection AddSingletonAlias<TContract, TImplementation>(this IServiceCollection services)
        where TContract : class
        where TImplementation : class, TContract
    {
        services.AddSingleton<TContract>(provider => provider.GetRequiredService<TImplementation>());
        return services;
    }
}
