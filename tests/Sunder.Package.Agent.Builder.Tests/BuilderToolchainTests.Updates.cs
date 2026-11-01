extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using Sunder.Sdk.Abstractions;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderToolchainTests
{
    [Theory]
    [InlineData(BuilderProjectKind.Dotnet)]
    [InlineData(BuilderProjectKind.Node)]
    public async Task ManagedUpdateWaitsForOldBuildLeaseAndNeverTouchesExecutionRuntimeRoots(BuilderProjectKind kind)
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.100"); fixture.AddDotnet("10.0.402");
        fixture.AddNode("24.18.1", "11.16.0"); fixture.AddNode("24.22.0", "11.19.1");
        var runtime = fixture.Project("execution-runtimes/older-major");
        await System.IO.File.WriteAllTextAsync(Path.Combine(runtime, "keep"), "installed package runtime");
        using var previous = fixture.Service(versions: new("10.0.100", "24.18.1", "11.16.0"));
        var old = await previous.EnsureAsync(kind);
        using var lease = await previous.AcquireLeaseAsync(old);
        using var current = fixture.Service();
        var replacementReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var update = current.UpgradeManagedAsync(kind, new InlineProgress(value =>
        {
            if (value.StartsWith("Ready:", StringComparison.Ordinal)) replacementReady.TrySetResult();
        }), deadline.Token);
        await replacementReady.Task.WaitAsync(deadline.Token);
        Assert.False(update.IsCompleted);
        Assert.True(Directory.Exists(old.RootPath));
        lease.Dispose();
        await update.WaitAsync(deadline.Token);
        Assert.False(Directory.Exists(old.RootPath));
        Assert.True(Assert.Single(await current.ListInstalledAsync(kind)).IsHealthy);
        Assert.Equal("installed package runtime", await System.IO.File.ReadAllTextAsync(Path.Combine(runtime, "keep")));
    }

    [Fact]
    public async Task UpdateDoesNotInstallOptionalDevelopmentToolsForTheFirstTime()
    {
        using var fixture = new Fixture();
        using var service = fixture.Service();
        await service.UpgradeManagedAsync(BuilderProjectKind.Dotnet);
        await service.UpgradeManagedAsync(BuilderProjectKind.Node);
        Assert.Equal(0, fixture.CatalogCalls);
        Assert.Equal(0, fixture.Downloads);
    }

    [Fact]
    public async Task FailedSdkReplacementPreservesPreviousManagedInstallation()
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.100"); fixture.AddDotnet("10.0.402");
        using var previous = fixture.Service(versions: new("10.0.100", "24.22.0", "11.19.1"));
        var old = await previous.EnsureAsync(BuilderProjectKind.Dotnet);
        fixture.CorruptDownload = true;
        using var current = fixture.Service();
        await Assert.ThrowsAsync<BuilderToolchainException>(() => current.UpgradeManagedAsync(BuilderProjectKind.Dotnet));
        Assert.True(Directory.Exists(old.RootPath));
        Assert.True(Assert.Single(await previous.ListInstalledAsync(BuilderProjectKind.Dotnet)).IsHealthy);
    }

    [Fact]
    public async Task NpmOnlyUpdateWaitsForTheExistingNodeBuildBeforeReplacingItsDirectory()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.16.0"); fixture.AddNpm("11.19.1");
        using var previous = fixture.Service(versions: new("10.0.402", "24.22.0", "11.16.0"));
        var old = await previous.EnsureAsync(BuilderProjectKind.Node);
        using var lease = await previous.AcquireLeaseAsync(old);
        using var current = fixture.Service();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var update = current.UpgradeManagedAsync(BuilderProjectKind.Node, new InlineProgress(value =>
        {
            if (value.StartsWith("Waiting for active", StringComparison.Ordinal)) waiting.TrySetResult();
        }), deadline.Token);
        await waiting.Task.WaitAsync(deadline.Token);
        Assert.False(update.IsCompleted);
        Assert.Equal("11.16.0", await System.IO.File.ReadAllTextAsync(old.NpmCliPath!));
        lease.Dispose();
        await update.WaitAsync(deadline.Token);
        Assert.Equal("11.19.1", Assert.Single(await current.ListInstalledAsync(BuilderProjectKind.Node)).NpmVersion);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NpmIsAnExactSeparatelyVerifiedPartOfTheDevelopmentToolchain(bool corrupt)
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.16.0");
        fixture.AddNpm("11.19.1");
        fixture.CorruptNpm = corrupt;
        using var service = fixture.Service();
        if (corrupt)
        {
            var error = await Assert.ThrowsAsync<BuilderToolchainException>(() => service.EnsureAsync(BuilderProjectKind.Node));
            Assert.Equal("npm", error.Phase);
            Assert.Empty(await service.ListInstalledAsync(BuilderProjectKind.Node));
        }
        else
        {
            var installed = await service.EnsureAsync(BuilderProjectKind.Node);
            Assert.Equal("11.19.1", installed.NpmVersion);
            Assert.Equal("11.19.1", await System.IO.File.ReadAllTextAsync(installed.NpmCliPath!));
            using var lease = await service.EnsureSelectedAsync(BuilderProjectKind.Node);
            Assert.Equal(installed.NpmCliPath, lease.Environment["npm_execpath"]);
        }
    }
}
