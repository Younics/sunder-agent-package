extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderLocalBackendTests
{
    [Theory]
    [InlineData("build", "net10.0", "Debug")]
    [InlineData("build", "net11.0", "Debug")]
    [InlineData("archive", "net10.0", "Release")]
    public async Task BuildUsesTheEvaluatedFrameworkWithoutOverridingIt(string operation, string framework, string configuration)
    {
        using var fixture = new Fixture();
        fixture.Process.EvaluationFramework = framework;
        var entry = fixture.WriteDotnet("project");
        await using var jobs = fixture.Jobs();
        var result = await TerminalAsync(jobs, jobs.Start(Request(operation, entry, BuilderProjectKind.Dotnet)));
        Assert.True(result.State == "succeeded", result.Error);
        Assert.Contains(Path.Combine(configuration, framework), operation == "archive" ? result.ArchivePath : result.DevelopmentFolder);
        var evaluation = Assert.Single(fixture.Process.Evaluations);
        Assert.Equal(fixture.Tools.For(BuilderProjectKind.Dotnet).ExecutablePath, evaluation.ExecutablePath);
        Assert.Contains("-p:Configuration=" + configuration, evaluation.Arguments);
        Assert.Contains("-p:RuntimeIdentifier=" + BuilderToolchainPlatform.Current.DotnetRid, evaluation.Arguments);
        var build = Assert.Single(fixture.Process.Requests);
        Assert.DoesNotContain(build.Arguments, argument => argument.StartsWith("-p:TargetFramework=", StringComparison.Ordinal));
    }
}
