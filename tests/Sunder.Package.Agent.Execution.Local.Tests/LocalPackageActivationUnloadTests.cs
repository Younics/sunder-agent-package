extern alias LocalWorker;

using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Worker;
using Xunit;
using AgentServices = Sunder.Generated.Packages.Younics_Ai_Agent_Services;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalPackageActivationUnloadTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GeneratedActivation_RetiresAfterOptionalDescription(bool describe)
    {
        using var scope = RegressionTestPackageScope.Create();
        AssertCollectible(CreateAndRetireUnusedActivation(scope.Context, describe));
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static WeakReference CreateAndRetireUnusedActivation(IPackageContext context, bool describe)
    {
        var activation = CreateActivation(context);
        if (describe) _ = activation.Target.DescribeAsync().AsTask().GetAwaiter().GetResult();
        return activation.RetireAsync().AsTask().GetAwaiter().GetResult();
    }

    [Fact]
    public async Task WorkerUpdate_RetiredActivationWithOutsideCapabilityCollectsAndReplacementOwnsOneUseCapability()
    {
        using var scope = RegressionTestPackageScope.Create();
        var workspaceRoot = Path.Combine(scope.RootPath, "workspace");
        var outsideRoot = Path.Combine(scope.RootPath, "outside");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideRoot);
        var outsidePath = Path.Combine(outsideRoot, "secret.txt");
        await File.WriteAllTextAsync(outsidePath, "outside-content");

        await using var replacement = CreateActivation(scope.Context);
        var replacementContext = IssueOutsideCapability(
            replacement.Target,
            workspaceRoot,
            outsidePath,
            "replacement-activation");
        var retired = CreateAndRetireActivation(scope.Context, workspaceRoot, outsidePath);

        Assert.False((await replacement.Target.ValidateResourceAuthorityAsync(retired.Context)).IsValid);
        Assert.True((await replacement.Target.ValidateResourceAuthorityAsync(replacementContext)).IsValid);

        var read = await replacement.Target.ReadFileAsync(replacementContext, new AgentFileReadRequest(outsidePath));

        Assert.False(read.IsError, read.ErrorMessage);
        Assert.Contains("outside-content", read.Content, StringComparison.Ordinal);
        Assert.False((await replacement.Target.ValidateResourceAuthorityAsync(replacementContext)).IsValid);
        AssertCollectible(retired.LoadContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static RetiredActivation CreateAndRetireActivation(
        IPackageContext packageContext,
        string workspaceRoot,
        string outsidePath)
    {
        // Keep collectible assemblies out of completed async state-machine boxes
        // retained by the asserting caller. All real asynchronous work is drained
        // inside this non-inlined lifetime boundary before checking collection.
        var activation = CreateActivation(packageContext);
        try
        {
            var context = IssueOutsideCapability(
                activation.Target,
                workspaceRoot,
                outsidePath,
                "retired-activation");
            Assert.True(activation.Target.ValidateResourceAuthorityAsync(context).AsTask().GetAwaiter().GetResult().IsValid);
            return new RetiredActivation(activation.RetireAsync().AsTask().GetAwaiter().GetResult(), context);
        }
        finally
        {
            activation.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
    }

    private static AgentExecutionTargetContext IssueOutsideCapability(
        AgentServices.IExecutionTargetService target,
        string workspaceRoot,
        string outsidePath,
        string activationId)
    {
        var now = DateTimeOffset.UtcNow;
        var workspaceId = Guid.NewGuid().ToString("N");
        var workspace = new AgentWorkspaceRecord(
            workspaceId,
            "Collectible Local activation",
            null,
            now,
            now,
            [new AgentWorkspacePathRecord(Guid.NewGuid().ToString("N"), workspaceId, workspaceRoot, true, 0, now, now)]);
        var binding = new AgentWorkspaceBindingRecord(
            Guid.NewGuid().ToString("N"),
            workspace.WorkspaceId,
            "agent.execution-targets",
            "local",
            "primary-execution-target",
            true,
            0,
            now,
            now);
        var operation = new AgentResourceOperationContext(
            Guid.NewGuid(),
            1,
            Guid.NewGuid().ToString("N"),
            "files.read",
            0,
            "workspace-generation",
            "binding-generation",
            "younics.ai.agent.tools.files",
            "younics.ai.agent.execution.local",
            activationId,
            1,
            CanIssueOutsideAuthority: true);
        var planningContext = new AgentExecutionTargetContext(
            Guid.NewGuid(),
            "profile",
            workspace,
            binding,
            AllowOutsideConfiguredScope: true)
        {
            ResourceOperation = operation,
        };
        var resource = target.ResolveFileAsync(planningContext, outsidePath)
            .AsTask()
            .GetAwaiter()
            .GetResult();
        return planningContext with
        {
            ApprovedResourceReferences = [resource.CanonicalReference],
            ApprovedResourceClaims = resource.ResourceClaim is null ? [] : [resource.ResourceClaim],
            ApprovedResourceCapabilities = resource.AuthorityReferences,
            ResourceOperation = operation with { CanIssueOutsideAuthority = false },
        };
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static CollectibleLocalActivation CreateActivation(IPackageContext packageContext)
    {
        var assemblyPath = typeof(LocalExecutionTarget).Assembly.Location;
        var loadContext = new LocalPackageLoadContext(assemblyPath);
        var assembly = loadContext.LoadFromAssemblyPath(assemblyPath);
        var services = new ServiceCollection();
        services.AddSingleton<IPackageContext>(packageContext);
        foreach (var serviceTypeName in new[]
                 {
                     "Sunder.Package.Agent.Execution.Local.LocalShellCatalogService",
                     "Sunder.Package.Agent.Execution.Local.LocalExecutionWorkspaceConfigService",
                     "Sunder.Package.Agent.Execution.Local.LocalExecutionTarget",
                 })
        {
            services.AddSingleton(assembly.GetType(serviceTypeName, throwOnError: true)!);
        }

        var provider = services.BuildServiceProvider();
        var targetType = Assert.Single(
            services.Select(static descriptor => descriptor.ServiceType),
            static type => string.Equals(
                type.FullName,
                "Sunder.Package.Agent.Execution.Local.LocalExecutionTarget",
                StringComparison.Ordinal));
        var implementation = provider.GetRequiredService(targetType);
        var registration = (SunderWorkerProviderRegistration)(implementation.GetType()
            .GetMethod("CreateSunderRpcProviderRegistration", BindingFlags.Instance | BindingFlags.Public)!
            .Invoke(implementation, null)
            ?? throw new InvalidOperationException("The Local execution target did not create its generated provider registration."));
        var catalog = new RegressionTestExtensionCatalog();
        catalog.AddGeneratedProvider("younics.ai.agent.execution.local", implementation, registration);
        var target = catalog.GetRequiredProviderAsync(
            catalog.Receiver("younics.ai.agent").Providers<AgentServices.IExecutionTargetService>())
            .AsTask().GetAwaiter().GetResult().Service;
        return new CollectibleLocalActivation(loadContext, provider, catalog, target, implementation, registration.Handler);
    }

    private static void AssertCollectible(WeakReference loadContext)
    {
        for (var attempt = 0; loadContext.IsAlive && attempt < 12; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            Thread.Sleep(20);
        }

        Assert.False(loadContext.IsAlive, loadContext is TrackedLoadContext tracked ? tracked.Diagnostics : null);
    }

    private sealed record RetiredActivation(WeakReference LoadContext, AgentExecutionTargetContext Context);

    private sealed class TrackedLoadContext(object context, IReadOnlyDictionary<string, WeakReference> objects)
        : WeakReference(context, trackResurrection: true)
    {
        internal string Diagnostics => string.Join(", ", objects.Select(pair => $"{pair.Key} alive={pair.Value.IsAlive}"));
    }

    private sealed class CollectibleLocalActivation : IAsyncDisposable
    {
        private LocalPackageLoadContext? _loadContext;
        private ServiceProvider? _provider;
        private RegressionTestExtensionCatalog? _catalog;
        private readonly IReadOnlyDictionary<string, WeakReference> _diagnostics;

        public CollectibleLocalActivation(
            LocalPackageLoadContext loadContext,
            ServiceProvider provider,
            RegressionTestExtensionCatalog catalog,
            AgentServices.IExecutionTargetService target,
            object implementation,
            object handler)
        {
            _loadContext = loadContext;
            _provider = provider;
            _catalog = catalog;
            Target = target;
            _diagnostics = new Dictionary<string, WeakReference>
            {
                ["provider"] = new(provider), ["catalog"] = new(catalog),
                ["client"] = new(target), ["implementation"] = new(implementation), ["handler"] = new(handler),
            };
        }

        public AgentServices.IExecutionTargetService Target { get; private set; }

        public async ValueTask<WeakReference> RetireAsync()
        {
            var loadContext = _loadContext
                ?? throw new ObjectDisposedException(nameof(CollectibleLocalActivation));
            var weakReference = new TrackedLoadContext(loadContext, _diagnostics);
            Target = null!;
            var catalog = _catalog;
            _catalog = null;
            if (catalog is not null)
            {
                await catalog.DisposeAsync();
            }

            _provider!.Dispose();
            _provider = null;
            _loadContext = null;
            loadContext.Unload();
            return weakReference;
        }

        public async ValueTask DisposeAsync()
        {
            if (_loadContext is not null)
            {
                _ = await RetireAsync();
            }
        }
    }

    private sealed class LocalPackageLoadContext : AssemblyLoadContext
    {
        private static readonly HashSet<string> SharedAssemblyNames =
        [
            typeof(IPackageContext).Assembly.GetName().Name!,
            typeof(IAgentExecutionTarget).Assembly.GetName().Name!,
            typeof(IServiceCollection).Assembly.GetName().Name!,
            typeof(ILoggerFactory).Assembly.GetName().Name!,
            typeof(SunderWorkerProviderRegistration).Assembly.GetName().Name!,
        ];
        private readonly string _assemblyDirectory;

        public LocalPackageLoadContext(string assemblyPath)
            : base(isCollectible: true)
        {
            _assemblyDirectory = Path.GetDirectoryName(assemblyPath)!;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (SharedAssemblyNames.Contains(assemblyName.Name ?? string.Empty))
            {
                return AssemblyLoadContext.Default.Assemblies.Single(assembly =>
                    AssemblyName.ReferenceMatchesDefinition(assembly.GetName(), assemblyName));
            }

            var candidate = Path.Combine(_assemblyDirectory, $"{assemblyName.Name}.dll");
            return File.Exists(candidate) ? LoadFromAssemblyPath(candidate) : null;
        }
    }
}
