extern alias DockerWorker;

using DockerWorker::Sunder.Agent.Execution.Common;
using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class DockerHostAccessPolicyTests
{
    [Fact]
    public void HostIdentityHasAnExplicitVersionedBindingAndNeverTranslatesAWindowsSidToAUid()
    {
        var policy = DockerHostAccessPolicy.Resolve();
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("1000:1000", policy.ContainerUser);
            Assert.Equal("windows-linux-nonroot-v1:1000:1000", policy.Signature);
        }
        else
        {
            var user = $"{LocalUnixNative.GetEffectiveUserId()}:{LocalUnixNative.GetEffectiveGroupId()}";
            Assert.Equal(user, policy.ContainerUser);
            Assert.Equal("host-posix-owner-v1:" + user, policy.Signature);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("daemon")]
    [InlineData("daemon windows")]
    [InlineData("daemon unknown")]
    [InlineData("daemon linux extra")]
    [InlineData("daemon\nother linux")]
    [InlineData("daemon\tother linux")]
    [InlineData("daemon linux\nother linux")]
    public void UnresolvedOrNonLinuxDaemonIsRejectedBeforeAnyContainerCanStart(string output)
        => Assert.Throws<InvalidOperationException>(() => DockerDaemonIdentity.Parse("endpoint", output));

    [Fact]
    public void DaemonIdentityBindsThePinnedEndpointAndTheLinuxEngine()
    {
        Assert.Equal("endpoint:linux:daemon", DockerDaemonIdentity.Parse("endpoint", "daemon linux\r\n"));
        Assert.NotEqual(DockerDaemonIdentity.Parse("endpoint", "daemon linux"), DockerDaemonIdentity.Parse("other-endpoint", "daemon linux"));
        Assert.NotEqual(DockerDaemonIdentity.Parse("endpoint", "daemon linux"), DockerDaemonIdentity.Parse("endpoint", "other-daemon linux"));
        Assert.Throws<InvalidOperationException>(() => DockerDaemonIdentity.Parse("endpoint", new string('x', 1024) + " linux"));
    }
}
