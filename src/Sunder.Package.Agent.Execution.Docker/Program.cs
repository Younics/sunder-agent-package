using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Execution.Docker;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;

SunderWorkerComposition? ownedComposition = null;
try
{
    await SunderWorker.RunAsync(context =>
    {
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(context);
        services.AddSingleton<DockerPackageStorageMigration>();
        services.AddSingleton<DockerCliRunner>();
        services.AddSingleton(provider => new DockerImageCatalogService(
            context,
            provider.GetRequiredService<DockerCliRunner>(),
            provider.GetRequiredService<DockerPackageStorageMigration>()));
        services.AddSingleton<DockerImageStackContributor>();
        services.AddSingleton(provider => new DockerExecutionWorkspaceConfigService(
            context,
            provider.GetRequiredService<DockerImageCatalogService>(),
            provider.GetRequiredService<DockerPackageStorageMigration>()));
        services.AddSingleton<DockerContainerLifecycleService>();
        services.AddSingleton<DockerExecutionTarget>();
        services.AddSingleton<DockerExecutionWorkspaceEditorContributor>();
        services.AddSingleton<DockerExecutionPresentationRpcHandler>();
        var serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        var migration = serviceProvider.GetRequiredService<DockerPackageStorageMigration>();
        var composition = new SunderWorkerComposition(serviceProvider, [migration]);
        ownedComposition = composition;
        var target = serviceProvider.GetRequiredService<DockerExecutionTarget>();

        return new SunderWorkerOptions(
        [
            RegisterAgent(
                "docker.execution.target",
                AgentRpcContractIds.ExecutionTarget,
                AgentExecutionTargetRpc.CreateHandler(target)),
            RegisterAgent(
                "docker.workspace.path.migrator",
                AgentRpcContractIds.WorkspacePathMigrator,
                AgentWorkspacePathMigratorRpc.CreateHandler(
                    serviceProvider.GetRequiredService<DockerExecutionWorkspaceConfigService>())),
            RegisterAgent(
                "docker.workspace.editor",
                AgentRpcContractIds.WorkspaceEditor,
                AgentWorkspaceEditorRpc.CreateHandler(
                    serviceProvider.GetRequiredService<DockerExecutionWorkspaceEditorContributor>())),
            new SunderWorkerProviderRegistration(
                "docker.stack",
                SunderStackContributorRpc.ContractId,
                SunderStackContributorRpc.ContractVersion,
                SunderStackContributorRpc.DescriptorSha256,
                SunderStackContributorRpc.AbiSha256,
                SunderStackContributorRpc.CreateHandler(
                    serviceProvider.GetRequiredService<DockerImageStackContributor>())),
            Register(
                DockerExecutionPresentationRpc.ProviderId,
                DockerExecutionPresentationRpc.Descriptor,
                DockerExecutionPresentationRpc.CreateHandler(
                    serviceProvider.GetRequiredService<DockerExecutionPresentationRpcHandler>())),
        ])
        {
            SettingsSchema = DockerExecutionConfiguration.Schema,
            Composition = composition,
        };
    });
}
finally
{
    if (ownedComposition is not null)
    {
        await ownedComposition.DisposeAsync();
    }
}

static SunderWorkerProviderRegistration RegisterAgent(
    string providerId,
    string contractId,
    ISunderRpcServiceHandler handler)
    => Register(providerId, AgentRpcContractDescriptors.Get(contractId), handler);

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
