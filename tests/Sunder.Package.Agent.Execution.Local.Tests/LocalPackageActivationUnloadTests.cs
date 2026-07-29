using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Sunder.Package.Agent.Contracts.Contracts;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Tests;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalPackageActivationUnloadTests
{
    [Fact]
    public async Task Update_RetiredActivationWithOutsideCapabilityCollectsAndReplacementOwnsOneUseCapability()
    {
        using var scope = RegressionTestPackageScope.Create();
        var workspaceRoot = Path.Combine(scope.RootPath, "workspace");
        var outsideRoot = Path.Combine(scope.RootPath, "outside");
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(outsideRoot);
        var outsidePath = Path.Combine(outsideRoot, "secret.txt");
        await File.WriteAllTextAsync(outsidePath, "outside-content");
        using var replacement = CreateActivation(scope.Context);
        var replacementContext = IssueOutsideCapability(
            replacement.Target,
            workspaceRoot,
            outsidePath,
            "replacement-activation");

        var retired = CreateAndRetireActivation(
            scope.Context,
            workspaceRoot,
            outsidePath);

        Assert.False((await replacement.Authority.ValidateResourceAuthorityAsync(retired.Context)).IsValid);
        Assert.True((await replacement.Authority.ValidateResourceAuthorityAsync(replacementContext)).IsValid);

        var read = await replacement.Target.ReadFileAsync(
            replacementContext,
            new AgentFileReadRequest(outsidePath));

        Assert.False(read.IsError, read.ErrorMessage);
        Assert.Contains("outside-content", read.Content, StringComparison.Ordinal);
        Assert.False((await replacement.Authority.ValidateResourceAuthorityAsync(replacementContext)).IsValid);
        AssertCollectible(retired.LoadContext);
    }

    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.NoOptimization)]
    private static RetiredActivation CreateAndRetireActivation(
        IPackageContext packageContext,
        string workspaceRoot,
        string outsidePath)
    {
        var activation = CreateActivation(packageContext);
        var context = IssueOutsideCapability(
            activation.Target,
            workspaceRoot,
            outsidePath,
            "retired-activation");
        Assert.True(activation.Authority.ValidateResourceAuthorityAsync(context).AsTask().GetAwaiter().GetResult().IsValid);
        return new RetiredActivation(activation.Retire(), context);
    }

    private static AgentExecutionTargetContext IssueOutsideCapability(
        IAgentExecutionTarget target,
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
            [
                new AgentWorkspacePathRecord(
                    Guid.NewGuid().ToString("N"),
                    workspaceId,
                    workspaceRoot,
                    true,
                    0,
                    now,
                    now),
            ]);
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
            "sunder.package.agent.tools.files",
            "sunder.package.agent.execution.local",
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
        var resource = target.ResolveFileResourceAsync(planningContext, outsidePath)
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
        var moduleType = assembly.GetType(
            "Sunder.Package.Agent.Execution.Local.PackageModule",
            throwOnError: true)!;
        var module = Assert.IsAssignableFrom<ISunderRuntimePackageModule>(Activator.CreateInstance(moduleType));
        var services = new ServiceCollection();
        module.ConfigureRuntimeServices(services, packageContext);
        services.AddSingleton<IPackageContext>(packageContext);
        var provider = services.BuildServiceProvider();
        var targetType = Assert.Single(
            services.Select(static descriptor => descriptor.ServiceType),
            static type => string.Equals(
                type.FullName,
                "Sunder.Package.Agent.Execution.Local.LocalExecutionTarget",
                StringComparison.Ordinal));
        var target = Assert.IsAssignableFrom<IAgentExecutionTarget>(provider.GetRequiredService(targetType));
        var authority = Assert.IsAssignableFrom<IAgentResourceAuthorityExecutionTarget>(target);
        return new CollectibleLocalActivation(loadContext, provider, target, authority);
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
        Assert.False(loadContext.IsAlive);
    }

    private sealed record RetiredActivation(
        WeakReference LoadContext,
        AgentExecutionTargetContext Context);

    private sealed class CollectibleLocalActivation : IDisposable
    {
        private LocalPackageLoadContext? _loadContext;
        private ServiceProvider? _provider;

        public CollectibleLocalActivation(
            LocalPackageLoadContext loadContext,
            ServiceProvider provider,
            IAgentExecutionTarget target,
            IAgentResourceAuthorityExecutionTarget authority)
        {
            _loadContext = loadContext;
            _provider = provider;
            Target = target;
            Authority = authority;
        }

        public IAgentExecutionTarget Target { get; private set; }

        public IAgentResourceAuthorityExecutionTarget Authority { get; private set; }

        public WeakReference Retire()
        {
            var loadContext = _loadContext
                ?? throw new ObjectDisposedException(nameof(CollectibleLocalActivation));
            var weakReference = new WeakReference(loadContext, trackResurrection: true);
            _provider!.Dispose();
            Target = null!;
            Authority = null!;
            _provider = null;
            _loadContext = null;
            loadContext.Unload();
            return weakReference;
        }

        public void Dispose()
        {
            if (_loadContext is not null)
            {
                _ = Retire();
            }
        }
    }

    private sealed class LocalPackageLoadContext : AssemblyLoadContext
    {
        private static readonly HashSet<string> SharedAssemblyNames =
        [
            typeof(ISunderRuntimePackageModule).Assembly.GetName().Name!,
            typeof(IAgentExecutionTarget).Assembly.GetName().Name!,
            typeof(IServiceCollection).Assembly.GetName().Name!,
            typeof(ILoggerFactory).Assembly.GetName().Name!,
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
