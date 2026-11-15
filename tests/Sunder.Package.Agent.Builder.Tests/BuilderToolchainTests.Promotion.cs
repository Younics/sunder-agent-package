extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderToolchainTests
{
    [WindowsFact("Windows denies directory rename while a child file lacks delete sharing.")]
    public async Task PromotionReusesVerifiedPayloadAfterChildFileIsReleased()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        using var handle = HoldVerifiedNode(fixture);
        var waited = false;
        var installed = await service.EnsureAsync(BuilderProjectKind.Node, progress: new InlineProgress(message =>
        {
            if (!message.StartsWith("Waiting briefly", StringComparison.Ordinal)) return;
            waited = true;
            handle.Dispose();
        }));

        Assert.True(waited);
        Assert.True(installed.IsHealthy);
        Assert.Equal(1, fixture.Downloads);
        Assert.Empty(Directory.EnumerateFileSystemEntries(fixture.PathOf("private-toolchains/staging")));
    }

    [WindowsFact("Windows promotion contention must terminate without adopting a partial installation.")]
    public async Task PersistentPromotionDenialIsBoundedAndReportsNativeError()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        using var handle = HoldVerifiedNode(fixture);
        var failure = await Assert.ThrowsAsync<BuilderToolchainException>(() =>
            service.EnsureAsync(BuilderProjectKind.Node).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("promotion", failure.Phase);
        Assert.Contains("Windows error 5", failure.Message);
        Assert.Contains("after 6 attempts", failure.Message);
        Assert.Empty(await service.ListInstalledAsync(BuilderProjectKind.Node));
        handle.Dispose();
    }

    [WindowsFact("Cancellation during replacement must restore the previous Windows installation.")]
    public async Task CancellationDuringPromotionRestoresPreviousInstallation()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        var previous = await service.EnsureAsync(BuilderProjectKind.Node);
        var receipt = await System.IO.File.ReadAllBytesAsync(Path.Combine(previous.RootPath, "builder-toolchain-receipt.json"));
        using var handle = HoldVerifiedNode(fixture);
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.RepairAsync(previous,
            new InlineProgress(message =>
            {
                if (message.StartsWith("Waiting briefly", StringComparison.Ordinal)) cancellation.Cancel();
            }), cancellation.Token));

        Assert.Equal(receipt, await System.IO.File.ReadAllBytesAsync(Path.Combine(previous.RootPath, "builder-toolchain-receipt.json")));
        Assert.True(Assert.Single(await service.ListInstalledAsync(BuilderProjectKind.Node)).IsHealthy);
        handle.Dispose();
    }

    [Fact]
    public async Task PromotionNeverMergesAnUnexpectedDestination()
    {
        using var fixture = new Fixture();
        var source = fixture.Project("source");
        var destination = fixture.Project("destination");
        await System.IO.File.WriteAllTextAsync(Path.Combine(destination, "keep"), "unowned");
        await Assert.ThrowsAsync<IOException>(() => BuilderToolchainPromotion.MoveAsync(
            fixture.Root, source, destination, null, CancellationToken.None));
        Assert.True(Directory.Exists(source));
        Assert.Equal("unowned", await System.IO.File.ReadAllTextAsync(Path.Combine(destination, "keep")));
    }

    [WindowsFact("A failed Windows repair must restore the existing receipt and payload.")]
    public async Task PersistentRepairDenialRestoresPreviousInstallation()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        var previous = await service.EnsureAsync(BuilderProjectKind.Node);
        var receiptPath = Path.Combine(previous.RootPath, "builder-toolchain-receipt.json");
        var receipt = await System.IO.File.ReadAllBytesAsync(receiptPath);
        using var handle = HoldVerifiedNode(fixture);
        var failure = await Assert.ThrowsAsync<BuilderToolchainException>(() =>
            service.RepairAsync(previous).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Equal("promotion", failure.Phase);
        Assert.Equal(receipt, await System.IO.File.ReadAllBytesAsync(receiptPath));
        Assert.True(Assert.Single(await service.ListInstalledAsync(BuilderProjectKind.Node)).IsHealthy);
        handle.Dispose();
    }

    [WindowsFact("An unexpected Windows destination must not be overwritten during rollback.")]
    public async Task FailedRollbackPreservesPreviousInstallationAndUnexpectedDestination()
    {
        using var fixture = new Fixture();
        fixture.AddNode("24.22.0", "11.19.1");
        using var service = fixture.Service();
        var previous = await service.EnsureAsync(BuilderProjectKind.Node);
        var receipt = await System.IO.File.ReadAllBytesAsync(Path.Combine(previous.RootPath, "builder-toolchain-receipt.json"));
        using var handle = HoldVerifiedNode(fixture);
        var failure = await Assert.ThrowsAsync<BuilderToolchainException>(() => service.RepairAsync(previous,
            new InlineProgress(message =>
            {
                if (!message.StartsWith("Waiting briefly", StringComparison.Ordinal)) return;
                Directory.CreateDirectory(previous.RootPath);
                System.IO.File.WriteAllText(Path.Combine(previous.RootPath, "keep"), "unexpected");
                handle.Dispose();
            })));

        Assert.Contains("promotion and rollback failed", failure.Message);
        var staging = Assert.Single(Directory.EnumerateDirectories(fixture.PathOf("private-toolchains/staging")));
        Assert.Equal(receipt, await System.IO.File.ReadAllBytesAsync(Path.Combine(staging, "previous", "builder-toolchain-receipt.json")));
        Assert.Equal("unexpected", await System.IO.File.ReadAllTextAsync(Path.Combine(previous.RootPath, "keep")));
        Assert.True(Directory.Exists(Path.Combine(staging, "payload")));
    }

    private static DeferredHandle HoldVerifiedNode(Fixture fixture)
    {
        var retained = new DeferredHandle();
        fixture.Process.BeforeVersionResult = () =>
        {
            var request = fixture.Process.Requests.Last();
            if (!request.ExecutablePath.Contains(Path.DirectorySeparatorChar + "staging" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return;
            if (retained.Handle is null)
                retained.Handle = System.IO.File.Open(request.ExecutablePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        };
        return retained;
    }

    private sealed class DeferredHandle : IDisposable
    {
        internal FileStream? Handle;
        public void Dispose() => Handle?.Dispose();
    }
}
