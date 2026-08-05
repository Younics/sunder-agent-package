using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Tools.Shell;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;

AgentRpcCatalog? ownedCatalog = null;
try
{
    await SunderWorkerV2.RunAsync(context =>
    {
        var catalog = AgentRpcCatalog.CreateDormant(context.Rpc);
        ownedCatalog = catalog;
        var source = new ShellToolSource();
        return new SunderWorkerV2Options(
        [
            Register(
                "shell.tools",
                AgentRpcContractIds.ToolSource,
                AgentToolSourceRpc.CreateHandler(source, catalog)),
            Register(
                "shell.permissions",
                AgentRpcContractIds.PermissionSurface,
                AgentPermissionSurfaceRpc.CreateHandler(source)),
            Register(
                "shell.prompt.context",
                AgentRpcContractIds.PromptContextContributor,
                AgentPromptContextContributorRpc.CreateHandler(source, catalog)),
        ])
        {
            OnActivated = (_, _) =>
            {
                catalog.StartWatching();
                return ValueTask.CompletedTask;
            },
            OnShutdown = (_, _) =>
            {
                catalog.Dispose();
                return ValueTask.CompletedTask;
            },
        };
    });
}
finally
{
    ownedCatalog?.Dispose();
}

static SunderWorkerProviderRegistration Register(
    string providerId,
    string contractId,
    ISunderRpcServiceHandler handler)
{
    var descriptor = AgentRpcContractDescriptors.Get(contractId);
    return new SunderWorkerProviderRegistration(
        providerId,
        descriptor.ContractId,
        descriptor.Version,
        descriptor.Sha256,
        handler);
}
