using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal partial class RegressionTestExtensionCatalog
{
    internal void AddRuntimeCatalog(object implementation)
        => AddProvider(AgentRpcContractIds.RuntimeCatalog, implementation, "sunder.package.agent");

    internal void AddChildRunExecutor(object implementation, string packageId = "sunder.package.agent")
        => AddProvider(AgentRpcContractIds.ChildRunExecutor, implementation, packageId);

    internal void AddGeneratedProvider(string packageId, object identity, SunderWorkerProviderRegistration registration)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _catalogDisposed) != 0, this);
        _client.State.AddGenerated(packageId, identity, registration, EmptyProviderLifetime.Instance, registration.ProviderId);
    }

    internal AgentRpcLease<TService> AcquireProvider<TService>(AgentRpcService<TService> service, object implementation, string packageId)
        where TService : class
    {
        var provider = AddProvider(service.ContractId, implementation, packageId);
        var handle = new AgentRpcProviderHandle(provider.PackageId, provider.PackageVersion, provider.ProviderId,
            provider.ContractId, provider.ContractVersion, provider.DescriptorSha256, provider.AbiSha256!, provider.ActivationId,
            provider.ActivationEpoch, provider.SessionGeneration, provider.Endpoint.Value);
        var reference = GetServiceReference(service, handle);
        Assert.True(reference.TryAcquire(out var lease), "The newly registered generated provider must be lease-ready.");
        return lease!;
    }

    public void AddProvider<TService>(
        AgentRpcService<TService> service,
        object implementation,
        string packageId = "test.package")
        where TService : class
        => AddProvider(service.ContractId, implementation, packageId);

    public void AddProvider<TService>(
        AgentRpcProviderService<TService> service,
        object implementation,
        string packageId = "test.package")
        where TService : class
        => AddProvider(service.ContractId, implementation, packageId);

    public void AddTool(object tool, string packageId = "test.package")
    {
        var source = new RegressionTestSingleToolSource(AgentRoleBoundary.As<IAgentTool>(tool));
        AddProvider(AgentRpcContractIds.ToolSource, source, packageId, tool);
    }

    public void AddBehaviorLoop(object implementation, string packageId = "test.package")
        => AddProvider(AgentRpcContractIds.BehaviorLoop, implementation, packageId);

    internal SunderRpcProviderSnapshot AddProvider(
        string contractId,
        object implementation,
        string packageId = "test.package",
        object? registrationIdentity = null,
        string? providerId = null)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _catalogDisposed) != 0, this);
        var providerCatalog = new AgentRpcCatalog(_client.ForCaller(packageId));
        IDisposable? lifetime = null;
        try
        {
            var created = _providers.Create(contractId, implementation, providerCatalog, _client.ForCaller(packageId));
            lifetime = created.Lifetime;
            if (AgentRoleBoundary.Unwrap(implementation).GetType().Assembly.EntryPoint?.DeclaringType?.FullName == "Sunder.Generated.Package.WorkerEntryPoint")
                providerId ??= created.Registration.ProviderId;
            return _client.State.AddGenerated(packageId, registrationIdentity ?? implementation, created.Registration,
                new ProviderLifetime(providerCatalog, lifetime), providerId);
        }
        catch
        {
            providerCatalog.Dispose();
            lifetime?.Dispose();
            throw;
        }
    }

    private sealed class EmptyProviderLifetime : IDisposable
    {
        internal static EmptyProviderLifetime Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class ProviderLifetime(AgentRpcCatalog catalog, IDisposable? implementation) : IDisposable
    {
        public void Dispose()
        {
            try { catalog.Dispose(); }
            finally { implementation?.Dispose(); }
        }
    }
}

internal sealed class RegressionTestSourceProviders : IDisposable
{
    private readonly RegressionTestProviderRoles _roles = new();

    internal (SunderWorkerProviderRegistration Registration, IDisposable? Lifetime) Create(
        string contract, object implementation, AgentRpcCatalog catalog, ISunderRpcClient rpc)
    {
        implementation = AgentRoleBoundary.Unwrap(implementation);
        var type = implementation.GetType().FullName;
        var assembly = implementation.GetType().Assembly.GetName().Name;
        if ((assembly is "Sunder.Package.Agent.Provider.Anthropic" or "Sunder.Package.Agent.Provider.Gemini"
            or "Sunder.Package.Agent.Provider.LMStudio" or "Sunder.Package.Agent.Provider.OpenAI")
            && (contract is AgentRpcContractIds.ChatProvider or AgentRpcContractIds.EmbeddingProvider))
        {
            var family = assembly["Sunder.Package.Agent.".Length..];
            return (_roles.Create(family, contract == AgentRpcContractIds.ChatProvider ? "Rpc.ChatProvider" : "Rpc.EmbeddingProvider", implementation), null);
        }
        if (contract == AgentRpcContractIds.ToolSource && (assembly is "Sunder.Package.Agent.Mcp" or "Sunder.Package.Agent.Subagents" or "Sunder.Package.Agent.Tools.Web"))
            return (_roles.Create(assembly["Sunder.Package.Agent.".Length..], "Rpc.ToolSourceProvider", implementation), null);
        // First-party tool implementations no longer implement the old central
        // local interface. Exercise their own provider/consumer lifetime together.
        if (type is "Sunder.Package.Agent.Tools.Files.FilesToolSource" or "Sunder.Package.Agent.Tools.Shell.ShellToolSource")
        {
            var files = type.Contains(".Files.", StringComparison.Ordinal);
            var family = files ? "Tools.Files" : "Tools.Shell";
            var provider = contract switch
            {
                AgentRpcContractIds.ToolSource => "ToolSourceProvider",
                AgentRpcContractIds.PermissionSurface => "PermissionSurfaceProvider",
                AgentRpcContractIds.PromptContextContributor => "PromptContextProvider",
                AgentRpcContractIds.SessionCleaner when files => "SessionCleanerProvider",
                _ => throw new ArgumentException($"The {family} source does not provide '{contract}'.", nameof(contract)),
            };
            if (contract is AgentRpcContractIds.PermissionSurface or AgentRpcContractIds.SessionCleaner)
                return (_roles.Create(family, "Rpc." + provider, implementation), null);
            var bindings = files
                ? _roles.CreateObject(family, "Rpc.FilesExecutionBindings", rpc, implementation)
                : AgentRoleBoundary.Create(implementation.GetType().Assembly.GetType(
                    "Sunder.Package.Agent.Tools.Shell.Rpc.ShellExecutionBindings", throwOnError: true)!, rpc);
            var lifetime = new AsyncLifetime((IAsyncDisposable)bindings);
            try
            {
                ((ValueTask)AgentRoleBoundary.Call(bindings, "StartAsync", CancellationToken.None)!).GetAwaiter().GetResult();
                return (_roles.Create(family, "Rpc." + provider, implementation, bindings), lifetime);
            }
            catch { lifetime.Dispose(); throw; }
        }

        SunderWorkerProviderRegistration registration = contract switch
        {
            AgentRpcContractIds.ChatProvider => _roles.Create("Provider.Gemini", "Rpc.ChatProvider", implementation),
            AgentRpcContractIds.EmbeddingProvider => _roles.Create("Provider.Gemini", "Rpc.EmbeddingProvider", implementation),
            AgentRpcContractIds.DurableLifecycleObserver => _roles.Create("Memory.Semantic", "Rpc.DurableLifecycleProvider", implementation),
            AgentRpcContractIds.ProfileCapabilityConsumer => _roles.Create("Memory.Semantic", "Rpc.ProfileCapabilityProvider", implementation),
            AgentRpcContractIds.SelectableCapabilityProvider => _roles.Create("Skills", "Rpc.SelectableCapabilityProvider", implementation),
            "sunder.agent.tool.execution.observed" => _roles.Create("Mcp", "Rpc.ObservedExecutionProvider", implementation),
            AgentRpcContractIds.WorkspacePathMigrator => _roles.Create("Execution.Local", "Rpc.WorkspacePathMigratorProvider", implementation),
            AgentRpcContractIds.WorkspaceEditor => _roles.Create("Execution.Local", "Rpc.WorkspaceEditorProvider", implementation),
            AgentRpcContractIds.ExecutionTarget when type == "Sunder.Package.Agent.Execution.Local.LocalExecutionTarget"
                => _roles.Create("Execution.Local", "Rpc.ExecutionTargetProvider", implementation),
            AgentRpcContractIds.ExecutionTarget when type == "Sunder.Package.Agent.Execution.Docker.DockerExecutionTarget"
                => _roles.Create("Execution.Docker", "Rpc.ExecutionTargetProvider", implementation),
            AgentRpcContractIds.ToolSource when type == "Sunder.Package.Agent.Skills.Services.SkillsFeature"
                => _roles.Create("Skills", "Rpc.ToolSourceProvider", implementation, implementation),
            AgentRpcContractIds.PromptContextContributor when type == "Sunder.Package.Agent.Skills.Services.SkillsFeature"
                => _roles.Create("Skills", "Rpc.PromptContextProvider", implementation, implementation),
            AgentRpcContractIds.BehaviorLoop when type == "Sunder.Package.Agent.Subagents.Services.OrchestratedAgentBehaviorLoop"
                => _roles.Create("Subagents", "Rpc.BehaviorLoopProvider", implementation, rpc),
            AgentRpcContractIds.RuntimeCatalog => new Rpc.Public.RuntimeCatalogService(As<IAgentRuntimeCatalog>()).CreateSunderRpcProviderRegistration(),
            AgentRpcContractIds.WorkspaceExecutionResolver => new Rpc.Public.WorkspaceExecutionResolverService(As<IAgentWorkspaceExecutionResolver>()).CreateSunderRpcProviderRegistration(),
            AgentRpcContractIds.ChildRunExecutor => new Rpc.Public.ChildRunExecutorService(As<IAgentChildRunExecutor>()).CreateSunderRpcProviderRegistration(),
            AgentRpcContractIds.SessionCleaner => new Rpc.Public.SessionCleanerService(As<IAgentSessionDataCleaner>()).CreateSunderRpcProviderRegistration(),
            AgentRpcContractIds.PromptContextContributor => new Rpc.Public.PromptContextContributorService(As<IAgentPromptContextContributor>(), catalog).CreateSunderRpcProviderRegistration(),
            AgentRpcContractIds.BehaviorLoop => new Rpc.Public.BehaviorLoopService(As<IAgentBehaviorLoop>(), catalog).CreateSunderRpcProviderRegistration(),
            AgentRpcContractIds.RunControl => new Rpc.Public.RunControlService((AgentRunControlRegistry)implementation).CreateSunderRpcProviderRegistration(),
            AgentRpcContractIds.SystemPromptContributor => Generated(new Sunder.Generated.Rpc.SystemPromptContributor.SunderAgentSystemPromptContributorRpcHandler(new RegressionTestSystemPromptProvider(As<IAgentSystemPromptContributor>()))),
            AgentRpcContractIds.PermissionSurface => Generated(new Sunder.Generated.Rpc.PermissionSurface.SunderAgentPermissionSurfaceRpcHandler(new RegressionTestPermissionProvider(As<IAgentPermissionSurface>()))),
            AgentRpcContractIds.ExecutionTarget => Generated(new Sunder.Generated.Rpc.ExecutionTarget.SunderAgentExecutionTargetRpcHandler(new RegressionTestExecutionProvider(As<IAgentExecutionTarget>()))),
            AgentRpcContractIds.ToolSource => Generated(new Sunder.Generated.Rpc.ToolSource.SunderAgentToolSourceRpcHandler(new RegressionTestToolProvider(As<IAgentToolSource>(), catalog))),
            _ => throw new ArgumentException($"No generated provider is available for '{contract}'.", nameof(contract)),
        };
        return (registration, null);

        T As<T>() => AgentRoleBoundary.As<T>(implementation);
        SunderWorkerProviderRegistration Generated(ISunderRpcServiceHandler handler)
            => RegressionTestProviderRoles.ForGeneratedHandler(contract, handler);
    }

    public void Dispose() => _roles.Dispose();

    private sealed class AsyncLifetime(IAsyncDisposable value) : IDisposable
    {
        private int _disposed;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Task.Run(async () => await value.DisposeAsync().ConfigureAwait(false)).GetAwaiter().GetResult();
        }
    }
}
