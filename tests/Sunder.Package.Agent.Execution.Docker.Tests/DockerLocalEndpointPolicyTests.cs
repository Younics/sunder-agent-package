extern alias DockerWorker;

using DockerWorker::Sunder.Package.Agent.Execution.Docker;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class DockerLocalEndpointPolicyTests
{
    [Theory]
    [InlineData("npipe:////server/pipe/docker_engine")]
    [InlineData("npipe://server/pipe/docker_engine")]
    [InlineData("npipe:////localhost/pipe/docker_engine")]
    [InlineData("npipe:////127.0.0.1/pipe/docker_engine")]
    [InlineData("npipe:////./pipe/")]
    [InlineData("npipe:////./pipe/../server/pipe/docker_engine")]
    [InlineData("npipe:////./pipe/docker_engine/../../server")]
    [InlineData("npipe:////./pipe/%2e%2e/server")]
    [InlineData("npipe:////./pipe/docker_engine?server=remote")]
    [InlineData("npipe:////./pipe/docker_engine#remote")]
    [InlineData("npipe:////./pipe/docker_engine\nother")]
    [InlineData("npipe://\\\\server\\pipe\\docker_engine")]
    [InlineData("npipe:////./pipe/a\\..\\remote")]
    [InlineData("npipe:////./pipe/a:b")]
    [InlineData("unix://server/var/run/docker.sock")]
    [InlineData("unix:////server/var/run/docker.sock")]
    [InlineData("unix:///")]
    [InlineData("unix:///var/run/docker.sock?remote=true")]
    [InlineData("tcp://127.0.0.1:2375")]
    [InlineData("ssh://user@host")]
    public void RejectsNonlocalOrAmbiguousEndpointsInBothIdentityAndDaemonOutput(string endpoint)
    {
        Assert.Equal("docker.endpoint.remote-unsupported",
            Assert.Throws<DockerExecutionDomainException>(() => DockerLocalEndpointPolicy.GetIdentity("default", endpoint)).Code);
        Assert.Equal("docker.endpoint.invalid",
            Assert.Throws<DockerExecutionDomainException>(() => DockerLocalEndpointPolicy.NormalizeEndpointReportedByDocker(endpoint)).Code);
    }

    [Theory]
    [InlineData("npipe:////./pipe/docker_engine")]
    [InlineData("npipe:////./pipe/dockerDesktopLinuxEngine")]
    [InlineData("NPIPE:////./PIPE/dockerDesktopLinuxEngine")]
    [InlineData("unix:///var/run/docker.sock")]
    [InlineData("unix:///Users/test/.docker/run/docker.sock")]
    public void AcceptsUnambiguousLocalEndpointsWithoutChangingTheirBinding(string endpoint)
    {
        Assert.Equal(endpoint, DockerLocalEndpointPolicy.NormalizeEndpointReportedByDocker(endpoint));
        Assert.Equal(endpoint, DockerLocalEndpointPolicy.NormalizeEndpointReportedByDocker("  " + endpoint + "  "));
        Assert.Equal(64, DockerLocalEndpointPolicy.GetIdentity("desktop-linux", endpoint).Length);
    }
}
