using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Contracts;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Package.Agent.Provider.TestSupport;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Provider.Gemini.Tests;

public sealed class GeminiPackageCompositionTests
{
    [Fact]
    public void RuntimeAndAppComposition_KeepProvidersAndEmbeddingsRuntimeOnly()
    {
        var context = new ProviderTestPackageContext("sunder.package.agent.provider.gemini");
        var runtimeServices = new ServiceCollection();
        var runtimeModule = new PackageModule();
        runtimeModule.ConfigureRuntimeServices(runtimeServices, context);

        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialRuntimeHandler));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(GeminiAgentProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(GeminiEmbeddingProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));
        Assert.Contains(runtimeServices, descriptor => descriptor.ServiceType == typeof(IAgentEmbeddingProvider));
        Assert.DoesNotContain(runtimeServices, descriptor => descriptor.ServiceType == typeof(GeminiSettingsViewModel));

        using var runtimeProvider = runtimeServices.BuildServiceProvider();
        var runtimeRegistry = new ProviderCompositionTestRegistry();
        runtimeModule.RegisterRuntimeContributions(runtimeRegistry, runtimeProvider);
        Assert.Equal(["gemini.chat", "gemini.embedding"], runtimeRegistry.RpcProviderIds);
        Assert.All(runtimeRegistry.RpcHandlerTypes, type => Assert.Equal(typeof(AgentRpcServiceHandler), type));
        Assert.Same(GeminiProviderConfiguration.Schema, Assert.Single(runtimeRegistry.SettingsSchemas));
        Assert.Equal(
            [ProviderCredentialRuntimeOperations.Query.OperationId, ProviderCredentialRuntimeOperations.Command.OperationId],
            runtimeRegistry.RuntimeOperationIds);
        Assert.All(runtimeRegistry.RuntimeOperationHandlerTypes, type => Assert.Equal(typeof(ProviderCredentialRuntimeHandler), type));

        var appServices = new ServiceCollection();
        appServices.AddSingleton<IPackageRuntimeClient>(NullPackageRuntimeClient.Instance);
        var appModule = new AppPackageModule();
        appModule.ConfigureAppServices(appServices, context);
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(GeminiSettingsViewModel));
        Assert.Contains(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAppRuntimeGateway));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialAccessor));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(ProviderCredentialRuntimeHandler));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(GeminiAgentProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(GeminiEmbeddingProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IAgentChatProvider));
        Assert.DoesNotContain(appServices, descriptor => descriptor.ServiceType == typeof(IAgentEmbeddingProvider));

        using var appProvider = appServices.BuildServiceProvider();
        var appRegistry = new ProviderCompositionTestRegistry();
        appModule.RegisterAppContributions(appRegistry, appProvider);
        Assert.Equal([typeof(GeminiSettingsView)], appRegistry.SettingsViewTypes);
        Assert.Empty(appRegistry.RpcProviderIds);
    }
}
