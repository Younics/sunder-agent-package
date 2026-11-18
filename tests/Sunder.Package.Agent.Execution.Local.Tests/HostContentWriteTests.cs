extern alias LocalWorker;

using System.Text;
using LocalModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

// Exercises the production write implementation below the unchanged Windows
// availability guard. Public/package acceptance is qualified separately.
public sealed class HostContentWriteTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(Path.Combine(TemporaryDirectory.GetPhysicalTemporaryRoot(),
        "sunder-content-write-" + Guid.NewGuid().ToString("N"))).FullName;

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("original")]
    [InlineData("longer 文-😀\r\nlast\n")]
    public async Task Update_PreservesIdentityAndReturnsFreshExactAuthority(string content)
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        using var before = HostSecurePathEngine.Capture([_root], path);
        LocalSecureApprovalLease? receipt = null;
        try
        {
            var result = await Write("file.txt", content, sink: value => receipt = value);
            Assert.False(result.IsError, result.Summary);
            Assert.NotNull(receipt);
            Assert.Equal(before.Binding.TargetIdentity, receipt.Binding.TargetIdentity);
            using var session = receipt.OpenSession(false, null, CancellationToken.None);
            using var target = session.OpenTarget();
            Assert.Equal(Encoding.UTF8.GetByteCount(content), target.Handle.Length);
            Assert.Equal(content, File.ReadAllText(path));
            Assert.Equal(["file.txt"], Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));
        }
        finally { receipt?.Dispose(); }
    }

    [Fact]
    public async Task Update_PreservesBomAndDoesNotDuplicateExplicitBom()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllBytes(path, Encoding.UTF8.GetBytes("\uFEFForiginal"));
        Assert.False((await Write("file.txt", "changed\r\nlast\n")).IsError);
        Assert.Equal("\uFEFFchanged\r\nlast\n", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        Assert.False((await Write("file.txt", "\uFEFFnext")).IsError);
        Assert.Equal("\uFEFFnext", Encoding.UTF8.GetString(File.ReadAllBytes(path)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteWrite_RetainsExactPartialFileAndReportsUncertainty(bool create)
    {
        var path = Path.Combine(_root, "file.txt");
        var original = new string('o', 150_000);
        if (!create) File.WriteAllText(path, original);
        using var before = create ? null : HostSecurePathEngine.Capture([_root], path);
        LocalSecureApprovalLease? receipt = null;
        try
        {
            var invoked = false;
            var result = await Write("file.txt", new string('s', 140_000), hooks: new Hook(checkpoint =>
            {
                if (checkpoint != LocalSecureFileSystemCheckpoint.AfterContentWriteChunk) return;
                invoked = true;
                throw new IOException("Synthetic write failure");
            }), sink: value => receipt = value);
            Assert.True(invoked);
            Assert.True(result.IsError);
            Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            Assert.Contains("partially modified", result.Summary, StringComparison.Ordinal);
            Assert.DoesNotContain("hidden", result.Summary, StringComparison.Ordinal);
            Assert.NotNull(receipt);
            if (before is not null) Assert.Equal(before.Binding.TargetIdentity, receipt.Binding.TargetIdentity);
            var bytes = File.ReadAllBytes(path);
            Assert.Equal(create ? 65_536 : original.Length, bytes.Length);
            Assert.All(bytes.Take(65_536), value => Assert.Equal((byte)'s', value));
            if (!create) Assert.All(bytes.Skip(65_536), value => Assert.Equal((byte)'o', value));
            Assert.Equal(["file.txt"], Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));
        }
        finally { receipt?.Dispose(); }
    }

    [Theory]
    [InlineData((int)LocalSecureFileSystemCheckpoint.BeforeContentLength)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterContentLength)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.AfterContentFileSync)]
    [InlineData((int)LocalSecureFileSystemCheckpoint.BeforeContentReceipt)]
    public async Task CompletionFailure_NeverReportsUnchangedOrDurableSuccess(int checkpointValue)
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        var invoked = false;
        var result = await Write("file.txt", "new", hooks: new Hook(checkpoint =>
        {
            if (checkpoint != (LocalSecureFileSystemCheckpoint)checkpointValue) return;
            invoked = true;
            throw new IOException("Synthetic completion failure");
        }), sink: value => value.Dispose());
        Assert.True(invoked);
        Assert.True(result.IsError);
        Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        Assert.StartsWith("new", File.ReadAllText(path), StringComparison.Ordinal);
        Assert.DoesNotContain("No mutation", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrecommitCancellation_PreservesOriginalBytes()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Write("file.txt", "new", cancellation.Token,
            new Hook(checkpoint => { if (checkpoint == LocalSecureFileSystemCheckpoint.BeforeContentWrite) cancellation.Cancel(); })));
        Assert.Equal("original", File.ReadAllText(path));
        using var root = HostSecurePathEngine.OpenRoot(_root);
        Assert.False(HostMutationCoordinator.IsEntered(root.Handle.Identity));
    }

    [Fact]
    public async Task CancellationAfterFirstChunk_CompletesBytesLengthFlushAndReceipt()
    {
        File.WriteAllText(Path.Combine(_root, "file.txt"), "original");
        using var cancellation = new CancellationTokenSource();
        var content = new string('n', 140_000);
        var captured = false;
        var result = await Write("file.txt", content, cancellation.Token,
            new Hook(checkpoint => { if (checkpoint == LocalSecureFileSystemCheckpoint.AfterContentWriteChunk) cancellation.Cancel(); }),
            value => { captured = true; value.Dispose(); });
        Assert.True(cancellation.IsCancellationRequested);
        Assert.True(captured);
        Assert.False(result.IsError, result.Summary);
        Assert.Equal(content, File.ReadAllText(Path.Combine(_root, "file.txt")));
    }

    [Fact]
    public async Task FinalContentCheck_RejectsStaleSnapshotWithoutWriting()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "newer");
        var result = await Write("file.txt", "replacement", hash: FileOperation.ComputeContentHash("original"));
        Assert.True(result.IsError);
        Assert.Equal(FileOperation.ContentChangedErrorCode, result.ErrorCode);
        Assert.Equal("newer", File.ReadAllText(path));
    }

    [Fact]
    public async Task CreateRace_PreservesArrivingFileWithoutAdoptingItsAuthority()
    {
        var path = Path.Combine(_root, "file.txt");
        var captured = false;
        var result = await Write("file.txt", "new", hooks: new Hook(checkpoint =>
        {
            if (checkpoint == LocalSecureFileSystemCheckpoint.BeforeContentWrite) File.WriteAllText(path, "foreign");
        }), sink: value => { captured = true; value.Dispose(); });
        Assert.True(result.IsError);
        Assert.Equal(FileOperation.FileExistsErrorCode, result.ErrorCode);
        Assert.False(captured);
        Assert.Equal("foreign", File.ReadAllText(path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateOpenRace_RejectsReplacementBeforeAnyDestructiveOpen(bool replaceWithDirectory)
    {
        var path = Path.Combine(_root, "file.txt");
        var moved = Path.Combine(_root, "original.txt");
        File.WriteAllText(path, "original");
        var raced = false;
        var result = await Write("file.txt", "replacement", hooks: new Hook(checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforeContentUpdateOpen) return;
            File.Move(path, moved);
            if (replaceWithDirectory)
            {
                Directory.CreateDirectory(path);
                File.WriteAllText(Path.Combine(path, "sentinel.txt"), "foreign");
            }
            else File.WriteAllText(path, "foreign");
            raced = true;
        }));
        Assert.True(raced);
        Assert.True(result.IsError);
        Assert.Equal("original", File.ReadAllText(moved));
        Assert.Equal("foreign", File.ReadAllText(replaceWithDirectory ? Path.Combine(path, "sentinel.txt") : path));
    }

    [Fact]
    public async Task Receipt_AllowsSecondUpdateWithoutChangingOriginalIdentity()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        using var before = HostSecurePathEngine.Capture([_root], path);
        LocalSecureApprovalLease? receipt = null;
        try
        {
            Assert.False((await Write("file.txt", "first", sink: value => receipt = value)).IsError);
            var result = await HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path, receipt),
                new LocalModels.AgentFileWriteRequest { Path = "file.txt", Content = "original", Overwrite = true,
                    ExpectedContentHash = FileOperation.ComputeContentHash("first") }, CancellationToken.None);
            Assert.False(result.IsError, result.Summary);
            using var after = HostSecurePathEngine.Capture([_root], path);
            Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
            Assert.Equal("original", File.ReadAllText(path));
        }
        finally { receipt?.Dispose(); }
    }

    [Fact]
    public async Task InvalidReplacementEncoding_FailsBeforeCreatingParents()
    {
        var result = await Write("missing/file.txt", new string('\ud800', 1));
        Assert.True(result.IsError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_root));
    }

    [Fact]
    public async Task CreatedFileParentSyncFailure_RetainsTheActualFileAndReportsUncertainty()
    {
        var invoked = false;
        LocalSecureApprovalLease? receipt = null;
        try
        {
            var result = await Write("created.txt", "created contents", hooks: new Hook(checkpoint =>
            {
                if (checkpoint != LocalSecureFileSystemCheckpoint.AfterCreatedFileParentSync) return;
                invoked = true;
                throw new IOException("Injected creation namespace completion failure.");
            }), sink: value => receipt = value);
            Assert.True(invoked);
            Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            Assert.Contains("completion or durability could not be confirmed", result.Summary, StringComparison.Ordinal);
            Assert.NotNull(receipt);
            using var actual = HostSecurePathEngine.Capture([_root], Path.Combine(_root, "created.txt"));
            Assert.Equal(actual.Binding.TargetIdentity, receipt.Binding.TargetIdentity);
            Assert.Equal("created contents", File.ReadAllText(Path.Combine(_root, "created.txt")));
            Assert.Equal(["created.txt"], Directory.EnumerateFileSystemEntries(_root).Select(Path.GetFileName));
        }
        finally { receipt?.Dispose(); }
    }

    [PlatformTheory(TestPlatform.Unix, "External Unix name/link changes must never turn failure handling into sanitization or pathname-based ownership.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task IncompleteCreate_RenamedOrLinkedObjectIsNeverSanitizedAndReplacementIsNotAdopted(bool hardLink)
    {
        var path = Path.Combine(_root, "file.txt");
        var alias = Path.Combine(_root, "retained.txt");
        LocalSecureApprovalLease? receipt = null;
        var invoked = false;
        try
        {
            var result = await Write("file.txt", new string('n', 140_000), hooks: new Hook(checkpoint =>
            {
                if (checkpoint != LocalSecureFileSystemCheckpoint.AfterContentWriteChunk) return;
                if (hardLink) TestFileSystemCapabilities.CreateHardLink(alias, path);
                else
                {
                    File.Move(path, alias);
                    File.WriteAllText(path, "unrelated replacement");
                }
                invoked = true;
                throw new IOException("Injected incomplete creation after an external name/link change.");
            }), sink: value => receipt = value);
            Assert.True(invoked);
            Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            Assert.Equal(new string('n', 65_536), File.ReadAllText(alias));
            if (hardLink)
            {
                Assert.Null(receipt); // Fresh receipt validation rejects the new multi-link state.
                Assert.Equal(new string('n', 65_536), File.ReadAllText(path));
            }
            else
            {
                Assert.NotNull(receipt);
                using var actual = HostSecurePathEngine.Capture([_root], alias);
                Assert.Equal(actual.Binding.TargetIdentity, receipt.Binding.TargetIdentity);
                Assert.Equal("unrelated replacement", File.ReadAllText(path));
            }
            Assert.DoesNotContain(Directory.EnumerateFileSystemEntries(_root), entry => HostSecurePathEngine.IsReservedName(Path.GetFileName(entry)));
        }
        finally { receipt?.Dispose(); }
    }

    [Fact]
    public async Task ReceiptConsumer_DoesNotInheritTheWriteFenceOrReentrantCoordinator()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        var consumed = false;
        var result = await Write("file.txt", "first", sink: authority =>
        {
            using (authority)
            using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
            {
                Assert.False(HostMutationCoordinator.IsEntered(authority.Binding.AnchorIdentity));
                var next = HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path, authority),
                    new LocalModels.AgentFileWriteRequest { Path = path, Content = "second", Overwrite = true,
                        ExpectedContentHash = FileOperation.ComputeContentHash("first") }, timeout.Token).GetAwaiter().GetResult();
                Assert.False(next.IsError, next.Summary);
                consumed = true;
            }
        });
        Assert.True(consumed);
        Assert.False(result.IsError, result.Summary);
        Assert.Equal("second", File.ReadAllText(path));
    }

    [PlatformFact(TestPlatform.NonRootUnix, "An existing Unix file can be edited without directory write permission.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task UnixUpdate_DoesNotNeedParentWriteAndRetainsMode()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead);
        File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            var result = await Write("file.txt", "changed");
            Assert.False(result.IsError, result.Summary);
            Assert.Equal("changed", File.ReadAllText(path));
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead, File.GetUnixFileMode(path));
        }
        finally { File.SetUnixFileMode(_root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute); }
    }

    [PlatformFact(TestPlatform.Unix, "User-file creation respects the same umask/default ACL as ordinary creation.")]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task UnixCreate_MatchesOrdinaryCreationMode()
    {
        var control = Path.Combine(_root, "control.txt");
        File.WriteAllText(control, "control");
        var result = await Write("file.txt", "created");
        Assert.False(result.IsError, result.Summary);
        Assert.Equal(File.GetUnixFileMode(control), File.GetUnixFileMode(Path.Combine(_root, "file.txt")));
    }

    private ValueTask<LocalModels.AgentFileMutationResult> Write(string name, string content,
        CancellationToken cancellationToken = default, ILocalSecureFileSystemHooks? hooks = null,
        Action<LocalSecureApprovalLease>? sink = null, string? hash = null)
    {
        var path = Path.Combine(_root, name);
        return HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path, PostMutationAuthoritySink: sink),
            new LocalModels.AgentFileWriteRequest { Path = name, Content = content, Overwrite = true, ExpectedContentHash = hash },
            cancellationToken, hooks: hooks);
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private sealed class Hook(Action<LocalSecureFileSystemCheckpoint> action) : ILocalSecureFileSystemHooks
    {
        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName) => action(checkpoint);
    }
}
