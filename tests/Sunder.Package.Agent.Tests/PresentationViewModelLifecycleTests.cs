extern alias McpApp;
extern alias MemoryApp;
extern alias SkillsApp;
extern alias SubagentsApp;

using Microsoft.Extensions.DependencyInjection;
using McpApp::Sunder.Package.Agent.Mcp;
using MemoryApp::Sunder.Package.Agent.Memory.Semantic.PackageViews;
using SkillsApp::Sunder.Package.Agent.Skills.PackageViews;
using SubagentsApp::Sunder.Package.Agent.Subagents.PackageViews;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class PresentationViewModelLifecycleTests
{
    [Fact]
    public void SubscribingViewModels_AreInjectedAsTransientDataContexts()
    {
        using var scope = RegressionTestPackageScope.Create();
        AssertTransientViewModel<MemoryApp::Sunder.Generated.Package.AppModule, MemoryInspectorViewModel, MemoryInspectorView>(scope);
        AssertTransientViewModel<McpApp::Sunder.Generated.Package.AppModule, AgentMcpSettingsViewModel, AgentMcpSettingsView>(scope);
        AssertTransientViewModel<SkillsApp::Sunder.Generated.Package.AppModule, SkillSettingsViewModel, SkillSettingsView>(scope);
        AssertTransientViewModel<SubagentsApp::Sunder.Generated.Package.AppModule, SubagentsViewModel, SubagentsView>(scope);
        AssertTransientViewModel<SubagentsApp::Sunder.Generated.Package.AppModule, SubsessionsViewModel, SubsessionsView>(scope);
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
