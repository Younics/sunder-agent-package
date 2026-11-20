extern alias LocalWorker;

using System.ComponentModel;
using System.Runtime.InteropServices;
using LocalModels = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformTheory(TestPlatform.Windows, "In-place edits require file-content rights, not parent write, delete, WRITE_DAC or WRITE_OWNER.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentUpdate_PreservesSecurityWithoutPolicyOrNamespaceWriteRights(bool partialFailure)
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = Assert.IsType<System.Security.Principal.SecurityIdentifier>(identity.User).Value;
        var directory = Path.Combine(_root, "no-parent-write");
        var path = Path.Combine(directory, "file.txt");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        // OW removes implicit owner WRITE_DAC; GRGW grants content rights but
        // not DELETE, WRITE_DAC or WRITE_OWNER. No elevation is involved.
        Assert.True(ConvertSecurityDescriptor($"D:P(A;;GRGW;;;{sid})(A;;GR;;;WD)(A;;RC;;;OW)", 1, out var descriptor, out _));
        Directory.CreateDirectory(directory);
        try
        {
            using var parent = root.Platform.OpenChild(root.Handle, "no-parent-write", root.Volume, true);
            using var file = LocalWindowsNative.OpenRelative(parent.Handle, "file.txt",
                LocalWindowsNative.GenericRead | LocalWindowsNative.GenericWrite, false, true, false, path,
                LocalWindowsNative.ShareAll, descriptor);
            RandomAccess.Write(file, "original"u8, 0);
            LocalWindowsNative.Flush(file, path);
        }
        finally { _ = FreeSecurityDescriptor(descriptor); }
        Assert.True(ConvertSecurityDescriptor($"D:P(A;;GR;;;{sid})(A;;RC;;;OW)", 1, out var parentDescriptor, out _));
        using var parentPolicy = LocalWindowsNative.OpenHandle(directory, LocalWindowsNative.WriteDac,
            LocalWindowsNative.OpenExisting, LocalWindowsNative.ShareAll);
        Assert.False(parentPolicy.IsInvalid);
        try
        {
            Assert.Equal(0, NtSetSecurityObject(parentPolicy, 0x80000004, parentDescriptor));
            // Query ordinary NtCreateFile access, not CreateFile's directory
            // BACKUP_SEMANTICS, which can exercise an elevated runner's privileges.
            var deniedParent = Assert.Throws<LocalSecurePathException>(() => LocalWindowsNative.OpenRelative(
                root.Handle.Handle, "no-parent-write", LocalWindowsNative.GenericWrite,
                true, false, false, directory, LocalWindowsNative.ShareAll));
            Assert.Equal(5, Assert.IsType<Win32Exception>(deniedParent.InnerException).NativeErrorCode);
            using var expected = root.Platform.OpenChild(root.Handle, "no-parent-write", root.Volume, true);
            foreach (var access in new[] { LocalWindowsNative.DeleteAccess, LocalWindowsNative.WriteDac, LocalWindowsNative.WriteOwner })
            {
                var denied = Assert.Throws<LocalSecurePathException>(() => LocalWindowsNative.OpenRelative(
                    expected.Handle, "file.txt", access, false, false, false, path, LocalWindowsNative.ShareAll));
                Assert.Equal(5, Assert.IsType<Win32Exception>(denied.InnerException).NativeErrorCode);
            }
            using var original = root.Platform.OpenChild(expected, "file.txt", root.Volume, false);
            using var before = LocalWindowsNative.CaptureSecurity(original.Handle, path);
            var content = new string('n', 140_000);
            var result = await HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path),
                new LocalModels.AgentFileWriteRequest { Path = path, Content = content, Overwrite = true }, CancellationToken.None,
                hooks: new ContentHook(checkpoint =>
                {
                    if (partialFailure && checkpoint == LocalSecureFileSystemCheckpoint.AfterContentWriteChunk)
                        throw new IOException("Synthetic incomplete edit");
                }));
            Assert.Equal(partialFailure, result.IsError);
            if (partialFailure) Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            using var final = root.Platform.OpenChild(expected, "file.txt", root.Volume, false);
            Assert.Equal(original.Identity, final.Identity);
            Assert.Equal(partialFailure ? 65_536 : content.Length, final.Length);
            using var after = LocalWindowsNative.CaptureSecurity(final.Handle, path);
            Assert.Equal(before.OwnerBytes, after.OwnerBytes);
            Assert.Equal(before.GroupBytes, after.GroupBytes);
            Assert.Equal(before.DaclBytes, after.DaclBytes);
            Assert.Equal(before.ProtectedDacl, after.ProtectedDacl);
            Assert.Equal(before.AccessPolicyBytes, after.AccessPolicyBytes);
            Assert.Equal(before.AccessPolicyControl, after.AccessPolicyControl);
        }
        finally
        {
            _ = FreeSecurityDescriptor(parentDescriptor);
            // Restore access only on this synthetic test-owned directory so its
            // normal fixture disposal can remove the test-owned file.
            Assert.True(ConvertSecurityDescriptor($"D:P(A;;FA;;;{sid})", 1, out var cleanup, out _));
            try
            {
                Assert.Equal(0, NtSetSecurityObject(parentPolicy, 0x80000004, cleanup));
            }
            finally { _ = FreeSecurityDescriptor(cleanup); }
        }
    }

    [PlatformFact(TestPlatform.Windows, "User creation must use normal inherited policy rather than an internal private staging DACL.")]
    public async Task UserFileCreation_MatchesOrdinaryCurrentUserCreationPolicy()
    {
        var path = Path.Combine(_root, "created.txt");
        File.WriteAllText(Path.Combine(_root, "control.txt"), "control");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var control = root.Platform.OpenChild(root.Handle, "control.txt", root.Volume, false);
        using var expectedSecurity = LocalWindowsNative.CaptureSecurity(control.Handle, control.FullPath);
        var result = await HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path),
            new LocalModels.AgentFileWriteRequest { Path = path, Content = "created", Overwrite = false }, CancellationToken.None);
        Assert.False(result.IsError, result.Summary);
        using var created = root.Platform.OpenChild(root.Handle, "created.txt", root.Volume, false);
        using var actual = LocalWindowsNative.CaptureSecurity(created.Handle, path);
        Assert.Equal(expectedSecurity.OwnerBytes, actual.OwnerBytes);
        Assert.Equal(expectedSecurity.GroupBytes, actual.GroupBytes);
        Assert.Equal(expectedSecurity.DaclBytes, actual.DaclBytes);
        Assert.Equal(expectedSecurity.ProtectedDacl, actual.ProtectedDacl);
        Assert.Equal(expectedSecurity.AccessPolicyBytes, actual.AccessPolicyBytes);
        Assert.Equal(expectedSecurity.AccessPolicyControl, actual.AccessPolicyControl);
    }

    [PlatformFact(TestPlatform.Windows, "Content editing must respect the original read-only attribute, not bypass it by replacement.")]
    public async Task ContentUpdate_ReadOnlyOriginalIsRejectedWithoutClearingItsAttribute()
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            using var before = HostSecurePathEngine.Capture([_root], path);
            var result = await HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path),
                new LocalModels.AgentFileWriteRequest { Path = path, Content = "replacement", Overwrite = true }, CancellationToken.None);
            Assert.True(result.IsError);
            Assert.NotEqual(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly));
            Assert.Equal("original", File.ReadAllText(path));
            using var after = HostSecurePathEngine.Capture([_root], path);
            Assert.Equal(before.Binding.TargetIdentity, after.Binding.TargetIdentity);
        }
        finally { File.SetAttributes(path, FileAttributes.Normal); }
    }

    [PlatformTheory(TestPlatform.Windows, "In-place updates retain unrelated NTFS stream, EA, compression and sparse metadata without copying it.")]
    [InlineData((int)FileAttributes.Compressed, false)]
    [InlineData((int)FileAttributes.Compressed, true)]
    [InlineData((int)FileAttributes.SparseFile, false)]
    [InlineData((int)FileAttributes.SparseFile, true)]
    public async Task ContentUpdate_RetainsAlternateStreamsExtendedAttributesAndStorage(int attributeValue, bool partialFailure)
    {
        var attribute = (FileAttributes)attributeValue;
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ":metadata", "unrelated stream");
        File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        var streamBytes = Enumerable.Range(0, 131_077).Select(index => (byte)(index % 251)).ToArray();
        File.WriteAllBytes(path + ":文-😀", streamBytes);
        WriteExtendedAttribute(path, "sunder.content", "retained value");
        WriteExtendedAttribute(path, "sunder.binary", "文-😀-\0-binary-value");
        using (var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            SetTestStorage(file, attribute, enabled: true);
        using (var stream = File.OpenHandle(path + ":metadata", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            SetTestStorage(stream, attribute, enabled: false);
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var original = root.Platform.OpenChild(root.Handle, "file.txt", root.Volume, false);
        var attributes = ReadExtendedAttributes(original.Handle);
        var injected = false;
        var result = await WriteContentAsync("file.txt", new string('n', 140_000), checkpoint: checkpoint =>
        {
            if (!partialFailure || checkpoint != LocalSecureFileSystemCheckpoint.AfterContentWriteChunk) return;
            injected = true;
            throw new IOException("Injected incomplete content update with independent metadata.");
        });
        Assert.Equal(partialFailure, injected);
        Assert.Equal(partialFailure, result.IsError);
        if (partialFailure) Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        using var updated = root.Platform.OpenChild(root.Handle, "file.txt", root.Volume, false);
        Assert.Equal(original.Identity, updated.Identity);
        Assert.Equal(new string('n', partialFailure ? 65_536 : 140_000), Read(updated));
        Assert.Equal(attributes, ReadExtendedAttributes(updated.Handle));
        Assert.True(File.GetAttributes(path).HasFlag(attribute));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
        Assert.Equal("unrelated stream", File.ReadAllText(path + ":metadata"));
        Assert.Equal("[ZoneTransfer]\r\nZoneId=3\r\n", File.ReadAllText(path + ":Zone.Identifier"));
        Assert.Equal(streamBytes, File.ReadAllBytes(path + ":文-😀"));
        using var retainedStream = File.OpenHandle(path + ":metadata", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (attribute == FileAttributes.Compressed) Assert.Equal(0, GetTestCompression(retainedStream));
        else Assert.False(GetTestSparse(retainedStream));
    }

    [PlatformTheory(TestPlatform.Windows, "Mapped readers may observe in-place bytes; a rejected mapped truncation must report partial mutation.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContentUpdate_MappedReaderObservesWritesAndLengthFailureIsExplicit(bool shrink)
    {
        var path = Path.Combine(_root, "file.txt");
        File.WriteAllText(path, new string('o', 131_072));
        using var stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var mapping = System.IO.MemoryMappedFiles.MemoryMappedFile.CreateFromFile(stream, null, 0,
            System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: true);
        using var view = mapping.CreateViewAccessor(0, 0, System.IO.MemoryMappedFiles.MemoryMappedFileAccess.Read);
        var content = new string('n', shrink ? 7 : 131_072);
        var result = await HostFileSystemExecutor.WriteFileCoreAsync(new([_root], path, path),
            new LocalModels.AgentFileWriteRequest { Path = path, Content = content, Overwrite = true }, CancellationToken.None);
        if (shrink)
        {
            Assert.True(result.IsError);
            Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            Assert.Contains("partially modified", result.Summary, StringComparison.Ordinal);
            Assert.Equal(131_072, new FileInfo(path).Length);
            Assert.Equal((byte)'o', view.ReadByte(content.Length));
        }
        else
        {
            Assert.False(result.IsError, result.Summary);
            Assert.Equal(content, File.ReadAllText(path));
        }
        Assert.Equal((byte)'n', view.ReadByte(0));
    }

    [DllImport("ntdll.dll")]
    private static extern int NtSetSecurityObject(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint information, IntPtr descriptor);

    private sealed class ContentHook(Action<LocalSecureFileSystemCheckpoint> action) : ILocalSecureFileSystemHooks
    {
        public void OnCheckpoint(LocalSecureFileSystemCheckpoint checkpoint, string parentPath, string? entryName) => action(checkpoint);
    }
}
