using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Provider.Gemini;
using Sunder.Package.Agent.Provider.Gemini.Protocol;
using Sunder.Package.Agent.Provider.Gemini.Protocol.Generated;
using Sunder.Package.Agent.Provider.Shared;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

ServiceProvider? ownedServices = null;
try
{
    await SunderWorker.RunAsync(context =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton(new ProviderCredentialAccessor(
            context.Secrets,
            GeminiProviderConfiguration.ApiKeySecretKey));
        services.AddSingleton<ProviderCredentialRpcProvider>();
        services.AddSingleton(serviceProvider => new GeminiAgentProvider(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        services.AddSingleton(serviceProvider => new GeminiEmbeddingProvider(
            context,
            serviceProvider.GetRequiredService<ProviderCredentialAccessor>()));
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        ownedServices = serviceProvider;

        return new SunderWorkerOptions(
        [
            Register(
                "gemini.chat",
                AgentRpcContractDescriptors.Get(AgentRpcContractIds.ChatProvider),
                AgentChatProviderRpc.CreateHandler(serviceProvider.GetRequiredService<GeminiAgentProvider>())),
            Register(
                "gemini.embedding",
                AgentRpcContractDescriptors.Get(AgentRpcContractIds.EmbeddingProvider),
                AgentEmbeddingProviderRpc.CreateHandler(serviceProvider.GetRequiredService<GeminiEmbeddingProvider>())),
            Register(
                "gemini.credentials",
                GeminiCredentialProtocol.Descriptor,
                new SunderPackageAgentProviderGeminiCredentialsRpcHandler(
                    serviceProvider.GetRequiredService<ProviderCredentialRpcProvider>())),
        ])
        {
            SettingsSchema = GeminiProviderConfiguration.Schema,
            OnShutdown = async (_, _) =>
            {
                await serviceProvider.DisposeAsync();
                if (ReferenceEquals(ownedServices, serviceProvider)) ownedServices = null;
            },
        };
    });
}
finally
{
    if (ownedServices is not null) await ownedServices.DisposeAsync();
}

static SunderWorkerProviderRegistration Register(
    string providerId,
    SunderRpcContractDescriptor descriptor,
    ISunderRpcServiceHandler handler)
    => new(
        providerId,
        descriptor.ContractId,
        descriptor.Version,
        descriptor.Sha256,
        descriptor.AbiSha256 ?? throw new InvalidOperationException(
            $"RPC contract '{descriptor.ContractId}' does not have a V2 ABI identity."),
        handler);
