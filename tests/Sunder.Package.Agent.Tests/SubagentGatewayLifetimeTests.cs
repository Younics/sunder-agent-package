using Sunder.Package.Agent.Subagents.Runtime;
using Sunder.Package.Agent.Subagents.PackageViews;
using Sunder.Sdk.Runtime;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed class SubagentGatewayLifetimeTests
{
    [Fact]
    public void AppRuntimeGateway_DisposeIsIdempotent()
    {
        var gateway = new SubagentAppRuntimeGateway(NullPackageRuntimeClient.Instance);

        gateway.Dispose();
        gateway.Dispose();
    }

    [Fact]
    public async Task SubsessionsViewModel_DisposeDoesNotDisposeSharedAppRuntimeGateway()
    {
        using var gateway = new SubagentAppRuntimeGateway(NullPackageRuntimeClient.Instance);
        var viewModel = new SubsessionsViewModel(gateway, gateway, gateway, gateway);

        viewModel.Dispose();

        await gateway.InitializeAsync();
    }
}
