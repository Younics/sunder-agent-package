using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Memory.Semantic.PackageViews;
using Sunder.Package.Agent.Skills.PackageViews;
using Sunder.Package.Agent.Subagents.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class PresentationViewModelLifecycleTests
{
    [Fact]
    public void SubscribingViewModels_AreInjectedAsTransientDataContexts()
    {
        using var scope = RegressionTestPackageScope.Create();
        AssertTransientViewModel<Sunder.Package.Agent.Memory.Semantic.AppPackageModule, MemoryInspectorViewModel, MemoryInspectorView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Mcp.AppPackageModule, AgentMcpSettingsViewModel, AgentMcpSettingsView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Skills.AppPackageModule, SkillSettingsViewModel, SkillSettingsView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Subagents.AppPackageModule, SubagentsViewModel, SubagentsView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Subagents.AppPackageModule, SubsessionsViewModel, SubsessionsView>(scope);
    }

    private static void AssertTransientViewModel<TModule, TViewModel, TView>(RegressionTestPackageScope scope)
        where TModule : Sunder.Sdk.Abstractions.ISunderAppPackageModule, new()
    {
        var services = new ServiceCollection();
        new TModule().ConfigureAppServices(services, scope.Context);

        var descriptor = Assert.Single(services, candidate => candidate.ServiceType == typeof(TViewModel));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.Contains(
            typeof(TView).GetConstructors(),
            constructor => constructor.GetParameters() is [{ ParameterType: var parameterType }]
                           && parameterType == typeof(TViewModel));
    }
}
