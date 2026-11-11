using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformFact(TestPlatform.Windows, TestFileSystemCapability.FileSymbolicLinks, "A reparse substituted at the final Windows mutation boundary must never redirect writes or cleanup.")]
    public void ReparseSourceRace_RejectsTheLinkAndPreservesTheOutsideTarget()
    {
        var workspace = Directory.CreateDirectory(Path.Combine(_root, "workspace")).FullName;
        var path = Path.Combine(workspace, "target.txt");
        var parked = Path.Combine(workspace, "original.txt");
        var outside = Path.Combine(_root, "outside.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(outside, "OUTSIDE");
        using var root = HostSecurePathEngine.OpenRoot(workspace);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var replaced = false;
        Assert.Throws<LocalSecureSymbolicLinkException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforePublishSyscall) return;
            File.Move(path, parked);
            File.CreateSymbolicLink(path, outside);
            replaced = true;
        }));
        Assert.True(replaced);
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        Assert.Equal("original", File.ReadAllText(parked));
        Assert.Equal("OUTSIDE", File.ReadAllText(outside));
        Assert.NotNull(File.ResolveLinkTarget(path, returnFinalTarget: false));
        Assert.Equal(0, RandomAccess.GetLength(temporary.Handle));
    }

    [PlatformFact(TestPlatform.Windows, "The freshly quarantined Windows file must still satisfy the approved content validator.")]
    public void ContentChangeBeforeQuarantine_RejectsStaleValidatorAndRestoresConcurrentBytes()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var changed = false;
        Assert.Throws<LocalSecureContentChangedException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforePublishSyscall) return;
            File.WriteAllText(path, "CONCURRENT");
            changed = true;
        }));
        Assert.True(changed);
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("CONCURRENT", Read(final));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
    }

    [PlatformTheory(TestPlatform.Windows, "Native directory deletion must retain contents and restore an exact directory identity across a conflicting-file rollback.")]
    [InlineData(false)]
    [InlineData(true)]
    public void DirectoryDelete_CommitsOrRestoresWithoutDestroyingConflictBytes(bool injectConflict)
    {
        var path = Directory.CreateDirectory(Path.Combine(_root, "folder")).FullName;
        File.WriteAllText(Path.Combine(path, "inside.txt"), "original-child");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "folder", root.Volume, false);
        Assert.Equal(LocalSecureNodeKind.Directory, expected.Kind);
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
        if (injectConflict) Assert.Throws<LocalSecurePathException>(Attempt);
        else Attempt();
        Assert.True(reached);
        Assert.Equal(injectConflict, Directory.Exists(path));
        using var retainedChild = root.Platform.OpenChild(expected, "inside.txt", root.Volume, false);
        Assert.Equal("original-child", Read(retainedChild));
        if (injectConflict)
        {
            using var restored = root.Platform.OpenChild(root.Handle, "folder", root.Volume, false);
            Assert.Equal(expected.Identity, restored.Identity);
            var conflict = Assert.Single(Directory.EnumerateFiles(_root), file => HostSecurePathEngine.IsQuarantineName(Path.GetFileName(file)));
            Assert.Equal("ATTACKER", File.ReadAllText(conflict));
        }
        else
        {
            var quarantined = Assert.Single(Directory.EnumerateDirectories(_root));
            Assert.True(HostSecurePathEngine.IsQuarantineName(Path.GetFileName(quarantined)));
            Assert.Equal("original-child", File.ReadAllText(Path.Combine(quarantined, "inside.txt")));
        }
    }

    [PlatformTheory(TestPlatform.Windows, "A default or alternate-stream writer must prevent a coherent Windows overwrite.")]
    [InlineData("")]
    [InlineData(":metadata")]
    public void ExistingWriter_RejectsMutationWithoutLosingEitherStream(string streamSuffix)
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ":metadata", "original-metadata");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var writer = File.OpenHandle(path + streamSuffix, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots));
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("original", Read(final));
        Assert.Equal("original-metadata", ReadPath(path + ":metadata"));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
    }

    [PlatformFact(TestPlatform.Windows, "The stream inventory must be revalidated after an adversarial publication checkpoint.")]
    public void NewSourceStreamBeforePublication_RejectsSnapshotAndRestoresExactOriginal()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var changed = false;
        Assert.Throws<LocalSecureContentChangedException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish) return;
            using var stream = LocalWindowsNative.OpenRelative(expected.Handle, ":late:$DATA", LocalWindowsNative.GenericRead | LocalWindowsNative.GenericWrite,
                false, true, true, path, LocalWindowsNative.ShareAll);
            RandomAccess.Write(stream, "CONCURRENT-METADATA"u8, 0);
            LocalWindowsNative.Flush(stream, path);
            changed = true;
        }));
        Assert.True(changed);
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("original", Read(final));
        Assert.Equal("CONCURRENT-METADATA", ReadPath(path + ":late"));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
    }

    [PlatformTheory(TestPlatform.Windows, "Cancellation before the first native mutation must leave the approved object untouched.")]
    [InlineData(false)]
    [InlineData(true)]
    public void CancellationBeforeQuarantine_DoesNotMoveTheOriginal(bool delete)
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var cancellation = new CancellationTokenSource();
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        var reached = false;
        void Cancel() { reached = true; cancellation.Cancel(); }
        if (delete)
            Assert.ThrowsAny<OperationCanceledException>(() => root.Platform.DeleteEntry(root.Handle, "target.txt", false, expected,
                null, slots, Cancel, static () => throw new Xunit.Sdk.XunitException("Quarantine must not begin."), cancellation.Token));
        else
        {
            using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
            Assert.ThrowsAny<OperationCanceledException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots,
                checkpoint => { if (checkpoint == LocalSecureFileSystemCheckpoint.BeforePublishSyscall) Cancel(); }, cancellation.Token));
            root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        }
        Assert.True(reached);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("original", Read(final));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Where(file => file != path).Select(ReadPath), text => text.Length != 0);
    }

    [PlatformFact(TestPlatform.Windows, "Metadata copying must be bounded before consuming a large alternate stream.")]
    public void OversizedAlternateStream_RetainsOriginalWithoutCopyingUnboundedBytes()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        const long oversizedLength = 64L * 1024 * 1024 + 1;
        using (var stream = File.OpenHandle(path + ":large", FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            RandomAccess.SetLength(stream, oversizedLength);
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var exception = Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots));
        Assert.Contains("metadata budget", exception.Message, StringComparison.Ordinal);
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("original", Read(final));
        using var large = File.OpenHandle(path + ":large", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal(oversizedLength, RandomAccess.GetLength(large));
        Assert.Equal(0, RandomAccess.GetLength(temporary.Handle));
    }

    [PlatformFact(TestPlatform.Windows, "NT opens and publication must not conflate siblings in a case-sensitive directory.")]
    public void CaseSensitiveDirectory_OverwritesOnlyTheExactCasedTarget()
    {
        using (var directory = LocalWindowsNative.OpenHandle(_root, LocalWindowsNative.GenericRead | LocalWindowsNative.GenericWrite,
                   LocalWindowsNative.OpenExisting, LocalWindowsNative.ShareAll))
        {
            Assert.False(directory.IsInvalid);
            var flags = 1U;
            Assert.True(SetCaseSensitiveInformation(directory, 23 /* FileCaseSensitiveInfo */, ref flags, sizeof(uint)),
                $"Could not enable native case sensitivity (Win32 {Marshal.GetLastPInvokeError()}).");
        }
        using var root = HostSecurePathEngine.OpenRoot(_root);
        Assert.True(root.Handle.CaseSensitiveDirectory);
        foreach (var (name, text) in new[] { ("Case.txt", "original"), ("case.txt", "LOWER-SENTINEL") })
        {
            using var created = root.Platform.CreateExclusiveFile(root.Handle, name, 0x180);
            RandomAccess.Write(created.Handle, System.Text.Encoding.UTF8.GetBytes(text), 0);
            root.Platform.Flush(created);
        }
        using var expected = root.Platform.OpenChild(root.Handle, "Case.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "new");
        Publish(root.Platform, root.Handle, temporary, "Case.txt", expected, slots);
        using var upper = root.Platform.OpenChild(root.Handle, "Case.txt", root.Volume, false);
        using var lower = root.Platform.OpenChild(root.Handle, "case.txt", root.Volume, false);
        Assert.Equal(temporary.Identity, upper.Identity);
        Assert.NotEqual(upper.Identity, lower.Identity);
        Assert.Equal("new", Read(upper));
        Assert.Equal("LOWER-SENTINEL", Read(lower));
    }

    [DllImport("kernel32.dll", EntryPoint = "SetFileInformationByHandle", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCaseSensitiveInformation(SafeFileHandle directory, int informationClass, ref uint flags, uint size);
}
