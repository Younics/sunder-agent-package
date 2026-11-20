using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformTheory(TestPlatform.Windows, "In-place success and incomplete writes must retain a non-default integrity policy on the same object.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DifferentIntegrityPolicy_RemainsAttachedToTheEditedObject(bool partialFailure)
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        SetLowIntegrityLabel(path);
        File.WriteAllText(Path.Combine(_root, "control.txt"), "control");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var control = root.Platform.OpenChild(root.Handle, "control.txt", root.Volume, false);
        using var before = LocalWindowsNative.CaptureSecurity(expected.Handle, path);
        using var ordinary = LocalWindowsNative.CaptureSecurity(control.Handle, control.FullPath);
        Assert.NotEmpty(before.AccessPolicyBytes);
        Assert.False(before.AccessPolicyBytes.AsSpan().SequenceEqual(ordinary.AccessPolicyBytes));
        var invoked = false;
        var result = await WriteContentAsync("target.txt", new string('n', 140_000), checkpoint: checkpoint =>
        {
            if (!partialFailure || checkpoint != LocalSecureFileSystemCheckpoint.AfterContentWriteChunk) return;
            invoked = true;
            throw new IOException("Injected incomplete content update.");
        });
        Assert.Equal(partialFailure, invoked);
        Assert.Equal(partialFailure, result.IsError);
        if (partialFailure) Assert.Equal(FileOperation.StrictMutationRecoveryRequiredErrorCode, result.ErrorCode);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var after = LocalWindowsNative.CaptureSecurity(final.Handle, path);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal(new string('n', partialFailure ? 65_536 : 140_000), Read(final));
        Assert.Equal(before.OwnerBytes, after.OwnerBytes);
        Assert.Equal(before.GroupBytes, after.GroupBytes);
        Assert.Equal(before.DaclBytes, after.DaclBytes);
        Assert.Equal(before.ProtectedDacl, after.ProtectedDacl);
        Assert.Equal(before.AccessPolicyBytes, after.AccessPolicyBytes);
        Assert.Equal(before.AccessPolicyControl, after.AccessPolicyControl);
    }

    [PlatformTheory(TestPlatform.Windows, "Concurrent security/EA updates are not copied over or restored by content editing.")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MetadataChangedDuringContentUpdate_RetainsTheConcurrentChange(bool changeSecurity)
    {
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        using var root = HostSecurePathEngine.OpenRoot(_root);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        var changed = false;
        var result = await WriteContentAsync("target.txt", "updated", checkpoint: checkpoint =>
        {
            if (checkpoint != LocalSecureFileSystemCheckpoint.BeforeContentWrite) return;
            using var edit = LocalWindowsNative.Reopen(expected.Handle, LocalWindowsNative.GenericRead
                | (changeSecurity ? LocalWindowsNative.WriteOwner : 0x10U /* FILE_WRITE_EA */), LocalWindowsNative.ShareAll, path);
            if (changeSecurity) SetLowIntegrityLabel(edit);
            else WriteExtendedAttribute(edit, "sunder.concurrent", "RETAIN-CONCURRENT-METADATA");
            changed = true;
        });
        Assert.True(changed);
        Assert.False(result.IsError, result.Summary);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("updated", Read(final));
        if (changeSecurity)
        {
            using var security = LocalWindowsNative.CaptureSecurity(final.Handle, path);
            Assert.NotEmpty(security.AccessPolicyBytes);
        }
        else Assert.Contains(ReadExtendedAttributes(final.Handle), entry => entry.Contains("SUNDER.CONCURRENT", StringComparison.OrdinalIgnoreCase));
    }

    private static void SetLowIntegrityLabel(string path)
    {
        using var file = LocalWindowsNative.OpenHandle(path, LocalWindowsNative.ReadControl | LocalWindowsNative.WriteOwner,
            LocalWindowsNative.OpenExisting, LocalWindowsNative.ShareAll);
        Assert.False(file.IsInvalid);
        SetLowIntegrityLabel(file);
    }

    private static void SetLowIntegrityLabel(SafeFileHandle file)
    {
        Assert.True(ConvertSecurityDescriptor("S:(ML;;NW;;;LW)", 1, out var descriptor, out _));
        try { Assert.Equal(0, NtSetSecurityObject(file, 0x10 /* LABEL_SECURITY_INFORMATION */, descriptor)); }
        finally { _ = FreeSecurityDescriptor(descriptor); }
    }

    private static void WriteExtendedAttribute(string path, string name, string value)
    {
        using var file = File.OpenHandle(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        WriteExtendedAttribute(file, name, value);
    }

    private static void WriteExtendedAttribute(SafeFileHandle file, string name, string value)
    {
        var nameBytes = Encoding.ASCII.GetBytes(name);
        var valueBytes = Encoding.UTF8.GetBytes(value);
        var buffer = new byte[8 + nameBytes.Length + 1 + valueBytes.Length];
        buffer[5] = checked((byte)nameBytes.Length);
        Assert.True(BitConverter.TryWriteBytes(buffer.AsSpan(6, 2), checked((ushort)valueBytes.Length)));
        nameBytes.CopyTo(buffer, 8);
        valueBytes.CopyTo(buffer, 8 + nameBytes.Length + 1);
        Assert.Equal(0, NtSetEaFile(file, out _, buffer, (uint)buffer.Length));
    }

    private static IReadOnlyList<string> ReadExtendedAttributes(SafeFileHandle file)
    {
        var buffer = new byte[64 * 1024];
        Assert.Equal(0, NtQueryEaFile(file, out var completion, buffer, (uint)buffer.Length, false,
            IntPtr.Zero, 0, IntPtr.Zero, true));
        var count = checked((int)completion.Information.ToInt64());
        Assert.InRange(count, 1, buffer.Length);
        var entries = new List<string>();
        var offset = 0;
        while (true)
        {
            Assert.InRange(offset, 0, count - 9);
            var nameLength = buffer[offset + 5];
            var valueLength = BitConverter.ToUInt16(buffer, offset + 6);
            Assert.InRange(9 + nameLength + valueLength, 9, count - offset);
            entries.Add($"{buffer[offset + 4]}:{Encoding.ASCII.GetString(buffer, offset + 8, nameLength)}:{Convert.ToHexString(buffer.AsSpan(offset + 9 + nameLength, valueLength))}");
            var next = checked((int)BitConverter.ToUInt32(buffer, offset));
            if (next == 0) break;
            offset += next;
        }
        return entries.Order(StringComparer.Ordinal).ToArray();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeCompletion
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("advapi32.dll", EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr FreeSecurityDescriptor(IntPtr descriptor);

    [DllImport("ntdll.dll")]
    private static extern int NtSetEaFile(SafeFileHandle file, out NativeCompletion completion, byte[] buffer, uint length);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryEaFile(SafeFileHandle file, out NativeCompletion completion, [Out] byte[] buffer, uint length,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry, IntPtr eaList, uint eaListLength, IntPtr eaIndex,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);
}
