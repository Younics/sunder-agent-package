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
        AssertTransientViewModel<Sunder.Package.Agent.Memory.Semantic.PackageModule, MemoryInspectorViewModel, MemoryInspectorView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Mcp.PackageModule, AgentMcpSettingsViewModel, AgentMcpSettingsView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Skills.PackageModule, SkillSettingsViewModel, SkillSettingsView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Subagents.PackageModule, SubagentsViewModel, SubagentsView>(scope);
        AssertTransientViewModel<Sunder.Package.Agent.Subagents.PackageModule, SubsessionsViewModel, SubsessionsView>(scope);
    }

    private static void AssertTransientViewModel<TModule, TViewModel, TView>(RegressionTestPackageScope scope)
        where TModule : Sunder.Sdk.Abstractions.ISunderPackageModule, new()
    {
        var services = new ServiceCollection();
        new TModule().ConfigureServices(services, scope.Context);

        var descriptor = Assert.Single(services, candidate => candidate.ServiceType == typeof(TViewModel));
        Assert.Equal(ServiceLifetime.Transient, descriptor.Lifetime);
        Assert.Contains(
            typeof(TView).GetConstructors(),
            constructor => constructor.GetParameters() is [{ ParameterType: var parameterType }]
                           && parameterType == typeof(TViewModel));
    }
}
