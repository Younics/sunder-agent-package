using Microsoft.Extensions.DependencyInjection;
using Sunder.Package.Agent.Mcp;
using Sunder.Package.Agent.Mcp.Services;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentMcpSettingsViewConstructionTests
{
    [Fact]
    public void SettingsView_ReceivesRegisteredViewModelWithConfigurationSync()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        new Sunder.Package.Agent.Mcp.AppPackageModule().ConfigureAppServices(services, scope.Context);
        var constructor = Assert.Single(
            typeof(AgentMcpSettingsView).GetConstructors(),
            candidate => candidate.GetParameters().Length > 0);

        Assert.Equal(
            typeof(AgentMcpSettingsViewModel),
            Assert.Single(constructor.GetParameters()).ParameterType);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(AgentMcpSettingsViewModel)
            && descriptor.ImplementationType == typeof(AgentMcpSettingsViewModel));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(McpSunderConfigurationSyncService));
    }
}
