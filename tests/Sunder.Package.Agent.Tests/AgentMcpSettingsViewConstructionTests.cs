extern alias McpApp;

using Microsoft.Extensions.DependencyInjection;
using McpApp::Sunder.Package.Agent.Mcp;
using McpApp::Sunder.Package.Agent.Mcp.Runtime;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Notifications;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class AgentMcpSettingsViewConstructionTests
{
    [Fact]
    public void SettingsView_ReceivesRuntimeBackedViewModel()
    {
        using var scope = RegressionTestPackageScope.Create();
        var services = new ServiceCollection();
        new McpApp::Sunder.Generated.Package.AppModule().ConfigureAppServices(services, scope.Context);
        var constructor = Assert.Single(
            typeof(AgentMcpSettingsView).GetConstructors(),
            candidate => candidate.IsDefined(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false));

        Assert.Equal(new[] { typeof(AgentMcpSettingsViewModel), typeof(IPackageContext), typeof(IPackageNotificationService) },
            constructor.GetParameters().Select(parameter => parameter.ParameterType));
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(AgentMcpSettingsViewModel)
            && descriptor.ImplementationFactory is not null);
        Assert.Contains(services, descriptor =>
            descriptor.ServiceType == typeof(McpAppRuntimeGateway));
        Assert.DoesNotContain(services, descriptor =>
            descriptor.ServiceType.Namespace?.Contains(".Services", StringComparison.Ordinal) == true);
    }
}
