using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformFact(TestPlatform.Windows, TestFileSystemCapability.FileSymbolicLinks, "A reparse substituted before writable acquisition must never redirect writes.")]
    public async Task ReparseSourceRace_RejectsTheLinkAndPreservesTheOutsideTarget()
    {
        var path = Path.Combine(_root, "target.txt");
        var parked = Path.Combine(_root, "original.txt");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(outside, "OUTSIDE");
        var replaced = false;
        var result = await WriteContentAsync("target.txt", "new", checkpoint: checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforeContentUpdateOpen) return;
            File.Move(path, parked);
            File.CreateSymbolicLink(path, outside);
            replaced = true;
        });
        Assert.True(replaced);
        Assert.True(result.IsError);
        Assert.Equal("original", File.ReadAllText(parked));
        Assert.Equal("OUTSIDE", File.ReadAllText(outside));
        Assert.NotNull(File.ResolveLinkTarget(path, returnFinalTarget: false));
    }

    [PlatformFact(TestPlatform.Windows, "The last content check after writable acquisition must reject a stale snapshot.")]
    public async Task ContentChangeBeforeWritableAcquisition_RejectsStaleValidator()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        var changed = false;
        var result = await WriteContentAsync("target.txt", "new", hash: FileOperation.ComputeContentHash("original"), checkpoint: checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforeContentUpdateOpen) return;
            File.WriteAllText(path, "CONCURRENT");
            changed = true;
        });
        Assert.True(changed);
        Assert.Equal(FileOperation.ContentChangedErrorCode, result.ErrorCode);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("CONCURRENT", Read(final));
    }

    [PlatformTheory(TestPlatform.Windows, "Deletion recovery must retain its own object without moving an unrelated replacement.")]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryDelete_RecoveryNeverMovesAnUnrelatedReplacement(bool injectConflict)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        File.WriteAllText(Path.Combine(path, "inside.txt"), "original-child");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "folder", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 2, CancellationToken.None);
        var reached = false;
        void Attempt() => root.Platform.DeleteEntry(root.Handle, "folder", true, expected, null, slots, static () => { }, () =>
        {
            reached = true;
            Assert.False(Directory.Exists(path));
            if (!injectConflict) return;
            File.WriteAllText(path, "ATTACKER");
            throw new LocalSecurePathException("Injected failure after directory quarantine.");
        }, CancellationToken.None);
        if (injectConflict) Assert.Throws<LocalSecureMutationRecoveryException>(Attempt);
        else Attempt();
        Assert.True(reached);
        Assert.False(Directory.Exists(path));
        using var retainedChild = root.Platform.OpenChild(expected, "inside.txt", root.Volume, false);
        Assert.Equal("original-child", Read(retainedChild));
        if (injectConflict)
        {
            Assert.Equal("ATTACKER", File.ReadAllText(path));
            Assert.Equal([path], Directory.EnumerateFiles(_root));
            var retained = Assert.Single(Directory.EnumerateDirectories(_root));
            Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(retained)));
            Assert.Equal("original-child", File.ReadAllText(Path.Combine(retained, "inside.txt")));
        }
        else
        {
            var quarantined = Assert.Single(Directory.EnumerateDirectories(_root));
            Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(quarantined)));
            Assert.Equal("original-child", File.ReadAllText(Path.Combine(quarantined, "inside.txt")));
        }
    }

    [PlatformTheory(TestPlatform.Windows, "A main-stream writer conflicts with the content fence; unrelated stream writers need not be blocked.")]
    [InlineData("")]
    [InlineData(":metadata")]
    public async Task ExistingWriter_RespectsPerStreamSharingWithoutLosingEitherStream(string streamSuffix)
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ":metadata", "original-metadata");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var writer = File.OpenHandle(path + streamSuffix, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        var result = await WriteContentAsync("target.txt", "updated");
        Assert.Equal(streamSuffix.Length == 0, result.IsError);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal(result.IsError ? "original" : "updated", Read(final));
        Assert.Equal("original-metadata", ReadPath(path + ":metadata"));
    }

    [PlatformFact(TestPlatform.Windows, "Content updates must not replace the file or discard newly added independent streams.")]
    public async Task NewStreamDuringContentUpdate_RemainsOnTheSameObject()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        var changed = false;
        var result = await WriteContentAsync("target.txt", "updated", checkpoint: checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforeContentWrite) return;
            using var stream = LocalWindowsNative.OpenRelative(expected.Handle, ":late:$DATA", LocalWindowsNative.GenericRead | LocalWindowsNative.GenericWrite,
                false, true, true, path, LocalWindowsNative.ShareAll);
            RandomAccess.Write(stream, "CONCURRENT-METADATA"u8, 0);
            LocalWindowsNative.Flush(stream, path);
            changed = true;
        });
        Assert.True(changed);
        Assert.False(result.IsError, result.Summary);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("updated", Read(final));
        Assert.Equal("CONCURRENT-METADATA", ReadPath(path + ":late"));
    }

    [PlatformFact(TestPlatform.Windows, "Cancellation before deletion's native mutation must leave the approved object untouched.")]
    public void DeleteCancellationBeforeQuarantine_DoesNotMoveTheOriginal()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var cancellation = new CancellationTokenSource();
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 2, CancellationToken.None);
        var reached = false;
        Assert.ThrowsAny<OperationCanceledException>(() => root.Platform.DeleteEntry(root.Handle, "target.txt", false, expected,
            null, slots, () => { reached = true; cancellation.Cancel(); },
            static () => throw new Xunit.Sdk.XunitException("Quarantine must not begin."), cancellation.Token));
        Assert.True(reached);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("original", Read(final));
    }

    [PlatformFact(TestPlatform.Windows, "Updating content must not copy or impose a copy-size limit on unrelated alternate streams.")]
    public async Task LargeAlternateStream_IsRetainedWithoutCopyingOrTruncatingIt()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        const long length = 64L * 1024 * 1024 + 1;
        using (var stream = File.OpenHandle(path + ":large", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
        {
            RandomAccess.SetLength(stream, length);
            RandomAccess.Write(stream, "start"u8, 0);
            RandomAccess.Write(stream, "end"u8, length - 3);
        }
        var result = await WriteContentAsync("target.txt", "updated");
        Assert.False(result.IsError, result.Summary);
        Assert.Equal("updated", File.ReadAllText(path));
        using var large = File.OpenHandle(path + ":large", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(length, RandomAccess.GetLength(large));
        var first = new byte[5]; var last = new byte[3];
        Assert.Equal(5, RandomAccess.Read(large, first, 0));
        Assert.Equal(3, RandomAccess.Read(large, last, length - 3));
        Assert.Equal("start"u8.ToArray(), first);
        Assert.Equal("end"u8.ToArray(), last);
    }

    [PlatformFact(TestPlatform.Windows, "NT content opens must not conflate siblings in a case-sensitive directory.")]
    public async Task CaseSensitiveDirectory_UpdatesOnlyTheExactCasedTarget()
    {
        using (var directory = LocalWindowsNative.OpenHandle(_root, LocalWindowsNative.GenericRead | LocalWindowsNative.GenericWrite,
                   LocalWindowsNative.OpenExisting, LocalWindowsNative.ShareAll))
        {
            Assert.False(directory.IsInvalid);
            var flags = 1U;
            Assert.True(SetCaseSensitiveInformation(directory, 23, ref flags, sizeof(uint)),
                $"Could not enable native case sensitivity (Win32 {Marshal.GetLastPInvokeError()}).");
        }
        using var root = HostSecurePathEngine.OpenRoot(_root);
        Assert.True(root.Handle.CaseSensitiveDirectory);
        foreach (var (name, text) in new[] { ("Case.txt", "original"), ("case.txt", "LOWER-SENTINEL") })
            Assert.False((await WriteContentAsync(name, text, overwrite: false)).IsError);
        using var expected = root.Platform.OpenChild(root.Handle, "Case.txt", root.Volume, false);
        var result = await WriteContentAsync("Case.txt", "new");
        Assert.False(result.IsError, result.Summary);
        using var upper = root.Platform.OpenChild(root.Handle, "Case.txt", root.Volume, false);
        using var lower = root.Platform.OpenChild(root.Handle, "case.txt", root.Volume, false);
        Assert.Equal(expected.Identity, upper.Identity);
        Assert.NotEqual(upper.Identity, lower.Identity);
        Assert.Equal("new", Read(upper));
        Assert.Equal("LOWER-SENTINEL", Read(lower));
    }

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCaseSensitiveInformation(SafeFileHandle directory, int informationClass, ref uint flags, uint size);
}
