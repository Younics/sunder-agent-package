using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformTheory(TestPlatform.Windows, "Windows writes must preserve compression/sparse storage state and retain originals on rollback.")]
    [InlineData((int)FileAttributes.Compressed, false)]
    [InlineData((int)FileAttributes.Compressed, true)]
    [InlineData((int)FileAttributes.SparseFile, false)]
    [InlineData((int)FileAttributes.SparseFile, true)]
    public void StorageMetadata_PreservesDefaultAndIndependentAlternateStreamState(int attributeValue, bool failAfterPublication)
    {
        var attribute = (FileAttributes)attributeValue;
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ":metadata", "original-metadata");
        using (var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            SetTestStorage(file, attribute, enabled: true);
        // Explicitly establish a different stream state, rather than relying on
        // whichever defaults NTFS chose when creating that named stream.
        using (var data = File.OpenHandle(path + ":metadata", FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))
            SetTestStorage(data, attribute, enabled: false);
        Assert.True(File.GetAttributes(path).HasFlag(attribute));
        File.SetAttributes(path, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive);
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var failureInjected = false;
        void Checkpoint(LocalSecureFileSystemCheckpoint checkpoint)
        {
            if (!failAfterPublication || checkpoint != LocalSecureFileSystemCheckpoint.AfterPublishedFileSync) return;
            failureInjected = true;
            throw new LocalSecurePathException("Injected failure after storage metadata preservation.");
        }
        if (failAfterPublication)
            Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint));
        else
            Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint);
        Assert.Equal(failAfterPublication, failureInjected);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(failAfterPublication ? expected.Identity : temporary.Identity, final.Identity);
        Assert.Equal(failAfterPublication ? "original" : "SECRET", Read(final));
        Assert.True(File.GetAttributes(path).HasFlag(attribute));
        Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReadOnly | FileAttributes.Hidden));
        Assert.Equal("original-metadata", ReadPath(path + ":metadata"));
        using (var data = File.OpenHandle(path + ":metadata", FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
        {
            if (attribute == FileAttributes.Compressed) Assert.Equal(0, GetTestCompression(data));
            else Assert.False(GetTestSparse(data));
        }
        if (failAfterPublication)
            Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
    }

    [PlatformFact(TestPlatform.Windows, "Rollback must sanitize copied streams through granted creation handles even when the published DACL denies a new write open.")]
    public void PublishedReadOnlyDacl_RollbackSanitizesStreamsWithoutReopeningForWrite()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ":metadata", "original-metadata");
        using var fixtureSecurity = LocalWindowsNative.OpenHandle(path, LocalWindowsNative.GenericRead | LocalWindowsNative.WriteDac,
            LocalWindowsNative.OpenExisting, LocalWindowsNative.ShareAll);
        Assert.False(fixtureSecurity.IsInvalid);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = Assert.IsType<System.Security.Principal.SecurityIdentifier>(identity.User).Value;
        SetTestDacl(fixtureSecurity, $"D:P(A;;FRSD;;;{sid})");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var originalSecurity = LocalWindowsNative.CaptureSecurity(expected.Handle, path);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        try
        {
            var failureInjected = false;
            Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, checkpoint =>
            {
                if (checkpoint != LocalSecureFileSystemCheckpoint.AfterPublishedFileSync) return;
                failureInjected = true;
                throw new LocalSecurePathException("Injected failure after a write-restricting DACL was published.");
            }));
            Assert.True(failureInjected);
            using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
            using var finalSecurity = LocalWindowsNative.CaptureSecurity(final.Handle, path);
            Assert.Equal(expected.Identity, final.Identity);
            Assert.Equal("original", Read(final));
            Assert.Equal(originalSecurity.DaclBytes, finalSecurity.DaclBytes);
            Assert.Equal("original-metadata", ReadPath(path + ":metadata"));
            Assert.Equal(0, RandomAccess.GetLength(temporary.Handle));
            var sanitized = Assert.Single(Directory.EnumerateFiles(_root), file => file != path);
            Assert.Equal(string.Empty, ReadPath(sanitized + ":metadata"));
        }
        finally
        {
            // Test teardown uses only the two fixture-owned authority handles,
            // not permissions that the production mutation was allowed to assume.
            SetTestDacl(temporary.Handle, $"D:P(A;;FA;;;{sid})");
            SetTestDacl(fixtureSecurity, $"D:P(A;;FA;;;{sid})");
        }
    }

    private static void SetTestDacl(SafeFileHandle handle, string sddl)
    {
        Assert.True(ConvertSecurityDescriptor(sddl, 1, out var descriptor, out _));
        try
        {
            Assert.True(GetTestDescriptorDacl(descriptor, out var present, out var dacl, out _));
            Assert.True(present);
            Assert.Equal(0U, SetFileSecurity(handle, 1, 0x80000004 /* protected DACL */, IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero));
        }
        finally { _ = FreeSecurityDescriptor(descriptor); }
    }

    private static void SetTestStorage(SafeFileHandle handle, FileAttributes attribute, bool enabled)
    {
        var code = attribute == FileAttributes.Compressed ? 0x0009c040U : 0x000900c4U;
        var input = attribute == FileAttributes.Compressed ? BitConverter.GetBytes((ushort)(enabled ? 2 : 0)) : new[] { (byte)(enabled ? 1 : 0) };
        Assert.True(TestStorageControl(handle, code, input, (uint)input.Length, null, 0, out _, IntPtr.Zero),
            $"Could not establish native storage state (Win32 {Marshal.GetLastPInvokeError()}).");
    }

    private static ushort GetTestCompression(SafeFileHandle handle)
    {
        var bytes = new byte[2];
        Assert.True(TestStorageControl(handle, 0x0009003c, null, 0, bytes, 2, out var count, IntPtr.Zero));
        Assert.Equal(2U, count);
        return BitConverter.ToUInt16(bytes);
    }

    private static bool GetTestSparse(SafeFileHandle handle)
    {
        Assert.True(GetTestStreamBasicInformation(handle, 0 /* FileBasicInfo */, out var basic, (uint)Marshal.SizeOf<TestStreamBasicInformation>()));
        return (basic.Attributes & (uint)FileAttributes.SparseFile) != 0;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TestStreamBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint Attributes;
    }

    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TestStorageControl(SafeFileHandle handle, uint code, byte[]? input, uint inputSize,
        [Out] byte[]? output, uint outputSize, out uint bytesReturned, IntPtr overlapped);

    [DllImport("kernel32.dll", EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTestStreamBasicInformation(SafeFileHandle handle, int informationClass, out TestStreamBasicInformation info, uint size);

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorDacl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTestDescriptorDacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr dacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);
}
