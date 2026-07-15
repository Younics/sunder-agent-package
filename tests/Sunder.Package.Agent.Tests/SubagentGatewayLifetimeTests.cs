using Sunder.Package.Agent.Subagents.Runtime;
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
}
