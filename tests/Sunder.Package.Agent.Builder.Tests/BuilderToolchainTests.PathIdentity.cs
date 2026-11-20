extern alias BuilderWorker;

using BuilderWorker::Sunder.Package.Agent.Builder;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderToolchainTests
{
    [Fact]
    public void ToolchainContainmentAcceptsAnExistingCaseAliasAndItsMissingChildren()
    {
        using var temporary = TemporaryDirectory.Create("builder-toolchain-alias");
        var original = temporary.GetPath("Workspace");
        var alternate = temporary.GetPath("workspace");
        Directory.CreateDirectory(original);
        var candidate = Directory.Exists(alternate) ? alternate : original;

        BuilderToolchainFiles.CheckPath(original, candidate);
        BuilderToolchainFiles.CheckPath(original, Path.Combine(candidate, "missing", "payload"));

        Assert.Empty(Directory.EnumerateFileSystemEntries(original));
    }

    [Fact]
    public async Task RestartThroughAnExistingStorageAliasPreservesCapturedOwnershipAndSuccessfulRemoval()
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.402");
        using var service = fixture.Service();
        var installed = await service.EnsureAsync(BuilderProjectKind.Dotnet);
        var alternate = fixture.PathOf("PRIVATE-TOOLCHAINS");
        var candidate = Directory.Exists(alternate) ? alternate : fixture.ManagedRoot;
        using var restarted = new BuilderToolchainService(candidate, new("10.0.402", "24.22.0", "11.19.1"),
            fixture, fixture, fixture.Process, _ => [], platform: fixture.Platform);
        BuilderToolchainInstallation selected;
        using (var lease = await restarted.AcquireLeaseAsync(installed))
        {
            selected = lease.Installation;
            Assert.True(selected.IsHealthy, selected.HealthError);
            Assert.Equal(installed.RootPath, selected.RootPath);
            Assert.Equal(installed.ExecutablePath, selected.ExecutablePath);
        }

        await restarted.RemoveAsync(selected);

        Assert.False(Directory.Exists(installed.RootPath));
        Assert.Equal(1, fixture.Downloads);
    }

    [Fact]
    public async Task CapturedStorageCoordinateCannotBeReboundByForgingItsNewPhysicalSpelling()
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.402");
        using var service = fixture.Service();
        var installed = await service.EnsureAsync(BuilderProjectKind.Dotnet);
        var newRoot = RenameDotnetKindFolder(installed);
        var forged = installed with { RootPath = newRoot, ExecutablePath = Path.Combine(newRoot, Path.GetFileName(installed.ExecutablePath)) };
        var probes = fixture.Process.Requests.Count;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            using var lease = await service.AcquireLeaseAsync(forged);
        });

        Assert.Equal(probes, fixture.Process.Requests.Count);
        Assert.True(System.IO.File.Exists(forged.ExecutablePath));
        // A fresh activation may capture a genuine filesystem alias, not a
        // different case-sensitive directory outside its canonical coordinate.
        using var restarted = fixture.Service();
        var current = await restarted.ListInstalledAsync(BuilderProjectKind.Dotnet);
        if (Directory.Exists(installed.RootPath))
        {
            var recaptured = Assert.Single(current);
            Assert.Equal(newRoot, recaptured.RootPath);
            using (var lease = await restarted.AcquireLeaseAsync(recaptured)) Assert.True(lease.Installation.IsHealthy);
            await restarted.RemoveAsync(recaptured);
            Assert.False(Directory.Exists(newRoot));
        }
        else
        {
            Assert.Empty(current);
            Assert.True(System.IO.File.Exists(forged.ExecutablePath));
        }
        Assert.Equal(1, fixture.Downloads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LeaseOrRemovalRechecksItsCapturedPhysicalCoordinateAfterWaitingForTheOperationLock(bool remove)
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.402");
        using var service = fixture.Service();
        var installed = await service.EnsureAsync(BuilderProjectKind.Dotnet);
        var before = System.IO.File.ReadAllBytes(installed.ExecutablePath);
        var probes = fixture.Process.Requests.Count;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lockPath = Path.Combine(service.RootPath, "locks", $"dotnet-sdk-{fixture.Platform.DotnetRid}-{installed.Version}.lock");
        using var operation = await BuilderToolchainLocks.Operation.AcquireAsync(lockPath, deadline.Token);
        async Task AcquireAsync() { using var lease = await service.AcquireLeaseAsync(installed, deadline.Token); }
        var pending = remove ? service.RemoveAsync(installed, cancellationToken: deadline.Token) : AcquireAsync();
        Assert.False(pending.IsCompleted);
        var newRoot = RenameDotnetKindFolder(installed);
        var aliasStillExists = Directory.Exists(installed.RootPath);
        operation.Dispose();

        var error = await Record.ExceptionAsync(() => pending.WaitAsync(deadline.Token));

        if (remove && !aliasStillExists) Assert.Null(error); // The captured coordinate is already absent; never remove its renamed sibling.
        else Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Equal(probes, fixture.Process.Requests.Count);
        Assert.Equal(before, System.IO.File.ReadAllBytes(Path.Combine(newRoot, Path.GetFileName(installed.ExecutablePath))));
    }

    [Fact]
    public async Task RepairNeverReplacesARenamedPhysicalTreeAfterTheArchiveDownloadAwait()
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.402");
        using var service = fixture.Service();
        var installed = await service.EnsureAsync(BuilderProjectKind.Dotnet);
        await System.IO.File.WriteAllTextAsync(Path.Combine(installed.RootPath, "keep"), "retained original installation");
        string? newRoot = null;
        var aliasStillExists = false;
        fixture.OnDownload = _ =>
        {
            newRoot = RenameDotnetKindFolder(installed);
            aliasStillExists = Directory.Exists(installed.RootPath);
            return Task.CompletedTask;
        };

        var error = await Record.ExceptionAsync(() => service.RepairAsync(installed));

        Assert.NotNull(newRoot);
        Assert.Equal("retained original installation", await System.IO.File.ReadAllTextAsync(Path.Combine(newRoot, "keep")));
        if (aliasStillExists)
        {
            var rejected = Assert.IsType<BuilderToolchainException>(error);
            Assert.Equal("promotion", rejected.Phase);
            Assert.Equal(newRoot, BuilderPhysicalPath.ReadExisting(installed.RootPath));
        }
        else
        {
            // On a case-sensitive filesystem the original coordinate is absent;
            // restoring it must not replace or clean up the distinct renamed tree.
            Assert.Null(error);
            Assert.True(Directory.Exists(installed.RootPath));
            Assert.False(System.IO.File.Exists(Path.Combine(installed.RootPath, "keep")));
            using var lease = await service.AcquireLeaseAsync(installed);
            Assert.True(lease.Installation.IsHealthy);
        }
    }

    [Fact]
    public async Task FirstInstallCannotReplaceNewCaseAliasStorageAfterItsDownloadAwait()
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.402");
        using var service = fixture.Service();
        var expectedRoot = Path.Combine(service.RootPath, "installed", "dotnet-sdk", fixture.Platform.DotnetRid, "10.0.402");
        var foreignRoot = Path.Combine(service.RootPath, "installed", "DOTNET-SDK", fixture.Platform.DotnetRid, "10.0.402");
        var aliasStillExists = false;
        fixture.OnDownload = async token =>
        {
            Directory.CreateDirectory(foreignRoot);
            await System.IO.File.WriteAllTextAsync(Path.Combine(foreignRoot, "keep"), "unapproved installation", token);
            aliasStillExists = Directory.Exists(expectedRoot);
        };
        BuilderToolchainInstallation? installed = null;

        var error = await Record.ExceptionAsync(async () => installed = await service.EnsureAsync(BuilderProjectKind.Dotnet));

        if (aliasStillExists)
        {
            var rejected = Assert.IsType<BuilderToolchainException>(error);
            Assert.Equal("promotion", rejected.Phase);
            Assert.Null(installed);
        }
        else
        {
            Assert.Null(error);
            Assert.NotNull(installed);
            Assert.Equal(expectedRoot, installed.RootPath);
            using var lease = await service.AcquireLeaseAsync(installed);
            Assert.True(lease.Installation.IsHealthy);
        }
        Assert.Equal("unapproved installation", await System.IO.File.ReadAllTextAsync(Path.Combine(foreignRoot, "keep")));
    }

    [Fact]
    public async Task LeaseCannotReturnHealthyAfterItsPhysicalDirectoryChangesDuringTheVersionProbe()
    {
        using var fixture = new Fixture();
        fixture.AddDotnet("10.0.402");
        using var service = fixture.Service();
        var installed = await service.EnsureAsync(BuilderProjectKind.Dotnet);
        var bytes = System.IO.File.ReadAllBytes(installed.ExecutablePath);
        var probes = fixture.Process.Requests.Count;
        string? newRoot = null;
        fixture.Process.BeforeVersionResult = () =>
        {
            fixture.Process.BeforeVersionResult = null;
            newRoot = RenameDotnetKindFolder(installed);
        };

        var error = await Record.ExceptionAsync(async () =>
        {
            using var lease = await service.AcquireLeaseAsync(installed);
        });

        Assert.NotNull(newRoot);
        Assert.True(error is InvalidOperationException or IOException, error?.ToString() ?? "The changed physical installation was admitted.");
        Assert.Equal(probes + 2, fixture.Process.Requests.Count);
        Assert.Equal(bytes, System.IO.File.ReadAllBytes(Path.Combine(newRoot, Path.GetFileName(installed.ExecutablePath))));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var lockPath = Path.Combine(service.RootPath, "locks", $"dotnet-sdk-{fixture.Platform.DotnetRid}-{installed.Version}.lock");
        using var operation = await BuilderToolchainLocks.Operation.AcquireAsync(lockPath, deadline.Token);
        using var mutation = operation.BeginMutation(); // A rejected selection must release its unreturned lease.
    }

    private static string RenameDotnetKindFolder(BuilderToolchainInstallation installed)
    {
        var platformFolder = Path.GetDirectoryName(installed.RootPath)!;
        var kindFolder = Path.GetDirectoryName(platformFolder)!;
        var renamedKind = Path.Combine(Path.GetDirectoryName(kindFolder)!, "DOTNET-SDK");
        Directory.Move(kindFolder, kindFolder + ".renaming");
        Directory.Move(kindFolder + ".renaming", renamedKind);
        return Path.Combine(renamedKind, Path.GetFileName(platformFolder), installed.Version);
    }

    [FileSystemTheory(TestFileSystemCapability.CaseSensitiveDirectories,
        "Case-distinct toolchain storage requires a case-sensitive test directory.")]
    [InlineData(false)]
    [InlineData(true)]
    public void ToolchainContainmentCannotAdmitACaseDistinctSibling(bool exists)
    {
        using var temporary = TemporaryDirectory.Create("builder-toolchain-containment");
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(temporary.Path);
        var root = temporary.GetPath("Workspace");
        var outside = temporary.GetPath("workspace");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(outside);
        var target = Path.Combine(outside, "payload");
        if (exists) System.IO.File.WriteAllText(target, "foreign installation");

        Assert.Throws<IOException>(() => BuilderToolchainFiles.CheckPath(root, target));

        BuilderToolchainFiles.CheckPath(root, Path.Combine(root, "new", "payload"));
        Assert.Empty(Directory.EnumerateFileSystemEntries(root));
        Assert.Equal(exists, System.IO.File.Exists(target));
        if (exists) Assert.Equal("foreign installation", System.IO.File.ReadAllText(target));
    }

    [FileSystemTheory(TestFileSystemCapability.CaseSensitiveDirectories,
        "Case-distinct installation authority requires a case-sensitive test directory.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopiedReceiptCannotGrantLeaseOrRemovalAuthorityToACaseDistinctWorkspace(bool remove)
    {
        using var fixture = new Fixture();
        TestFileSystemCapabilities.EnableCaseSensitiveDirectory(fixture.Root);
        fixture.AddDotnet("10.0.402");
        using var service = fixture.Service();
        var installed = await service.EnsureAsync(BuilderProjectKind.Dotnet);
        var foreignRoot = Path.Combine(fixture.PathOf("Private-toolchains"), Path.GetRelativePath(fixture.ManagedRoot, installed.RootPath));
        Directory.CreateDirectory(foreignRoot);
        foreach (var path in Directory.GetFiles(installed.RootPath, "*", SearchOption.AllDirectories))
        {
            var copy = Path.Combine(foreignRoot, Path.GetRelativePath(installed.RootPath, path));
            Directory.CreateDirectory(Path.GetDirectoryName(copy)!);
            System.IO.File.Copy(path, copy);
        }
        var originalBytes = System.IO.File.ReadAllBytes(installed.ExecutablePath);
        var foreignExecutable = Path.Combine(foreignRoot, Path.GetFileName(installed.ExecutablePath));
        var foreignReceipt = Path.Combine(foreignRoot, BuilderToolchainFiles.ReceiptName);
        var receiptBytes = System.IO.File.ReadAllBytes(foreignReceipt);
        var forged = installed with { RootPath = foreignRoot, ExecutablePath = foreignExecutable };
        var probes = fixture.Process.Requests.Count;
        var downloads = fixture.Downloads;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
        {
            if (remove) await service.RemoveAsync(forged);
            else using (await service.AcquireLeaseAsync(forged)) { }
        });

        Assert.Equal(originalBytes, System.IO.File.ReadAllBytes(installed.ExecutablePath));
        Assert.Equal(originalBytes, System.IO.File.ReadAllBytes(foreignExecutable));
        Assert.Equal(receiptBytes, System.IO.File.ReadAllBytes(foreignReceipt));
        Assert.Equal(probes, fixture.Process.Requests.Count);
        Assert.Equal(downloads, fixture.Downloads);
    }
}
