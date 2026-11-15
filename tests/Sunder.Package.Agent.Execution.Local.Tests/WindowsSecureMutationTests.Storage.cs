using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformFact(TestPlatform.Windows, "A write-denying DACL must reject an edit without replacement or policy alteration.")]
    public async Task ContentUpdate_WriteDenyingDaclRetainsOriginalAndIndependentStreams()
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        File.WriteAllText(path + ":metadata", "original-metadata");
        using var fixtureSecurity = LocalWindowsNative.OpenHandle(path, LocalWindowsNative.GenericRead | LocalWindowsNative.WriteDac,
            LocalWindowsNative.OpenExisting, LocalWindowsNative.ShareAll);
        Assert.False(fixtureSecurity.IsInvalid);
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = Assert.IsType<System.Security.Principal.SecurityIdentifier>(identity.User).Value;
        SetTestDacl(fixtureSecurity, $"D:P(A;;FRSD;;;{sid})(A;;RC;;;OW)");
        try
        {
            using var root = HostSecurePathEngine.OpenRoot(_root);
            using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
            using var before = LocalWindowsNative.CaptureSecurity(expected.Handle, path);
            var result = await WriteContentAsync("target.txt", "replacement");
            Assert.True(result.IsError);
            Assert.NotEqual(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
            using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
            using var after = LocalWindowsNative.CaptureSecurity(final.Handle, path);
            Assert.Equal(expected.Identity, final.Identity);
            Assert.Equal("original", Read(final));
            Assert.Equal(before.DaclBytes, after.DaclBytes);
            Assert.Equal("original-metadata", ReadPath(path + ":metadata"));
            Assert.Equal(["target.txt"], Directory.EnumerateFiles(_root).Select(Path.GetFileName));
        }
        finally { SetTestDacl(fixtureSecurity, $"D:P(A;;FA;;;{sid})"); }
    }

    private static void SetTestDacl(SafeFileHandle handle, string sddl)
    {
        Assert.True(ConvertSecurityDescriptor(sddl, 1, out var descriptor, out _));
        try { Assert.Equal(0, NtSetSecurityObject(handle, 0x80000004, descriptor)); }
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
        Assert.True(GetTestStreamBasicInformation(handle, 0, out var basic, (uint)Marshal.SizeOf<TestStreamBasicInformation>()));
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
}
