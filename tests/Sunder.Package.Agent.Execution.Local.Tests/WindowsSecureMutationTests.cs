extern alias LocalWorker;

using System.Runtime.Versioning;
using Xunit;
using LocalModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;

namespace Sunder.Package.Agent.Execution.Local.Tests;

// Native/content qualification exercises the production backend directly;
// public Local/Docker consumer acceptance is separate.
[SupportedOSPlatform("windows")]
public sealed partial class WindowsSecureMutationTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("sunder-windows-mutations-").FullName;

    [PlatformFact(TestPlatform.Windows, "Private challenge creation, directory flush and exact-handle rename/deletion remain native operations.")]
    public void NativeHandles_CreateFlushRenameAndDeleteWithoutPathFallbacks()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var platform = Assert.IsType<LocalWindowsFileSystemPlatform>(root.Platform);
        var parked = _root + "-parked";
        var parentMoved = false;
        try { Directory.Move(_root, parked); parentMoved = true; }
        catch (IOException) { }
        finally { if (parentMoved) Directory.Move(parked, _root); }
        Assert.False(parentMoved, "A retained Windows ancestor must deny delete sharing and prevent reparenting.");
        using var directory = platform.CreateDirectory(root.Handle, "created");
        using var created = platform.CreateExclusiveFile(directory, "private.txt", 0x180);
        RandomAccess.Write(created.Handle, "secret"u8, 0);
        platform.Flush(created);
        platform.Flush(directory);
        using var security = LocalWindowsNative.CaptureSecurity(created.Handle, created.FullPath);
        Assert.True(security.ProtectedDacl);
        Assert.NotEmpty(security.DaclBytes);
        Assert.Equal(1, BitConverter.ToUInt16(security.DaclBytes, 4));
        Assert.Throws<LocalSecureFileAlreadyExistsException>(() => platform.CreateExclusiveFile(directory, "private.txt", 0x180));
        Assert.Throws<IOException>(() => File.Move(created.FullPath, Path.Combine(directory.FullPath, "stolen.txt")));
        LocalWindowsNative.RenameWithinParentNoReplace(created.Handle, "published.txt", created.FullPath);
        platform.Flush(directory);
        using var read = platform.OpenChild(directory, "published.txt", root.Volume, requireDirectory: false);
        Assert.Equal(created.Identity, read.Identity);
        Assert.Equal("secret", Read(read));
        Assert.False(platform.IsUnlinked(created));
        LocalWindowsNative.Delete(created.Handle, created.FullPath);
        created.Dispose();
        platform.Flush(directory);
        Assert.True(platform.IsUnlinked(read));
        Assert.False(File.Exists(Path.Combine(directory.FullPath, "published.txt")));
    }

    [PlatformFact(TestPlatform.Windows, TestFileSystemCapability.HardLinks, "A hard link substituted before writable acquisition must not gain content authority.")]
    public async Task ContentUpdate_HardLinkSubstitutionLeavesBothObjectsUnchanged()
    {
        var path = Path.Combine(_root, "target.txt");
        var parked = Path.Combine(_root, "approved.txt");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(outside, "OUTSIDE");
        var invoked = false;
        var result = await WriteContentAsync("target.txt", "new", checkpoint: checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforeContentUpdateOpen) return;
            File.Move(path, parked);
            TestFileSystemCapabilities.CreateHardLink(path, outside);
            invoked = true;
        });
        Assert.True(invoked);
        Assert.True(result.IsError);
        Assert.Equal("original", File.ReadAllText(parked));
        Assert.Equal("OUTSIDE", File.ReadAllText(path));
        Assert.Equal("OUTSIDE", File.ReadAllText(outside));
    }

    [PlatformFact(TestPlatform.Windows, "Created directory cleanup must use its creation identity without sharing conflicts.")]
    public void CreatedDirectory_RetiresOnlyItsExactIdentity()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var ownership = new LocalCreatedDirectoryAuthority();
        using var child = ownership.CreateChild(root.Platform, root.Handle, "created", root.Volume);
        Assert.True(ownership.OwnsChild(root.Handle, child));
        Assert.False(child.MutationExclusive);
        Assert.ThrowsAny<IOException>(() => Directory.Move(child.FullPath, Path.Combine(_root, "foreign-rename")));
        Assert.Throws<LocalSecurePathException>(() => LocalWindowsNative.RenameWithinParentNoReplace(
            child.Handle, "borrower-rename", child.FullPath));
        using var siblingBorrow = ownership.CreateChild(root.Platform, root.Handle, "created", root.Volume);
        Assert.Equal(child.Identity, siblingBorrow.Identity);
        ownership.Dispose();
        Assert.Empty(ownership.CleanupFailures);
        Assert.False(Directory.Exists(Path.Combine(_root, "created")));
        var retired = Assert.Single(Directory.EnumerateDirectories(_root));
        Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(retired)));
        Assert.Empty(Directory.EnumerateFileSystemEntries(retired));
    }

    [PlatformFact(TestPlatform.Windows, "Content creation/update must preserve exact UTF-16 names without encoding fallback.")]
    public async Task ContentWrites_UseExactUtf16ChildNames()
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        foreach (var name in new[] { "文-😀-e\u0301.txt", "lone-" + new string('\ud800', 1) + ".txt" })
        {
            var created = await WriteContentAsync(name, "name-content", overwrite: false);
            Assert.False(created.IsError, created.Summary);
            using var before = root.Platform.OpenChild(root.Handle, name, root.Volume, false);
            var updated = await WriteContentAsync(name, "updated");
            Assert.False(updated.IsError, updated.Summary);
            using var after = root.Platform.OpenChild(root.Handle, name, root.Volume, false);
            Assert.Equal(before.Identity, after.Identity);
            Assert.Contains(root.Platform.EnumerateNames(root.Handle), actual => string.Equals(actual, name, StringComparison.Ordinal));
            Assert.Equal("updated", Read(after));
        }
    }

    private ValueTask<LocalModels.AgentFileMutationResult> WriteContentAsync(string name, string content,
        bool overwrite = true, string? hash = null, Action<LocalSecureFileSystemCheckpoint>? checkpoint = null,
        CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_root, name);
        return HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path),
            new LocalModels.AgentFileWriteRequest { Path = name, Content = content, Overwrite = overwrite, ExpectedContentHash = hash },
            cancellationToken, hooks: checkpoint is null ? null : new ContentHook(checkpoint));
    }

    private static string Read(LocalSecureHandle handle)
    {
        using var stream = new FileStream(handle.DuplicateHandle(), FileAccess.Read);
        stream.Position = 0;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static string ReadPath(string path)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var stream = new FileStream(file, FileAccess.Read);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, recursive: true);
    }
}
