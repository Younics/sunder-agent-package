using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class WindowsSecureMutationTests
{
    [PlatformTheory(TestPlatform.Windows, "Windows overwrites must preserve non-default integrity policy, including publication rollback.")]
    [InlineData(false)]
    [InlineData(true)]
    public void DifferentIntegrityPolicy_PreservesSecurityAndPrivateStaging(bool failAfterPublication)
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        SetLowIntegrityLabel(path);
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var original = LocalWindowsNative.CaptureSecurity(expected.Handle, path);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        using var staged = LocalWindowsNative.CaptureSecurity(temporary.Handle, temporary.FullPath);
        Assert.NotEmpty(original.AccessPolicyBytes);
        Assert.False(original.AccessPolicyBytes.AsSpan().SequenceEqual(staged.AccessPolicyBytes));
        var checkedPrivate = false;
        var failureInjected = false;
        void Checkpoint(LocalSecureFileSystemCheckpoint checkpoint)
        {
            if (checkpoint == LocalSecureFileSystemCheckpoint.AfterValidationBeforePublish)
            {
                using var stillPrivate = LocalWindowsNative.CaptureSecurity(temporary.Handle, temporary.FullPath);
                Assert.Equal(staged.OwnerBytes, stillPrivate.OwnerBytes);
                Assert.Equal(staged.DaclBytes, stillPrivate.DaclBytes);
                Assert.Equal(staged.AccessPolicyBytes, stillPrivate.AccessPolicyBytes);
                checkedPrivate = true;
            }
            if (failAfterPublication && checkpoint == LocalSecureFileSystemCheckpoint.AfterPublishedFileSync)
            {
                failureInjected = true;
                throw new LocalSecurePathException("Injected failure after security transfer.");
            }
        }
        if (failAfterPublication)
            Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint));
        else
            Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint);
        Assert.True(checkedPrivate);
        Assert.Equal(failAfterPublication, failureInjected);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        using var finalSecurity = LocalWindowsNative.CaptureSecurity(final.Handle, path);
        Assert.Equal(failAfterPublication ? expected.Identity : temporary.Identity, final.Identity);
        Assert.Equal(failAfterPublication ? "original" : "SECRET", Read(final));
        Assert.Equal(original.OwnerBytes, finalSecurity.OwnerBytes);
        Assert.Equal(original.GroupBytes, finalSecurity.GroupBytes);
        Assert.Equal(original.AccessPolicyBytes, finalSecurity.AccessPolicyBytes);
        Assert.Equal(original.AccessPolicyControl, finalSecurity.AccessPolicyControl);
        Assert.Equal(original.DaclBytes, finalSecurity.DaclBytes);
        if (failAfterPublication)
            Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
    }

    [PlatformTheory(TestPlatform.Windows, "Windows overwrites must copy NTFS extended attributes and retain original recovery metadata.")]
    [InlineData(false)]
    [InlineData(true)]
    public void ExtendedAttributes_PreserveValuesOnOverwriteAndRollback(bool failAfterPublication)
    {
        using var root = HostSecurePathEngine.OpenRoot(_root);
        var path = Path.Combine(_root, "target.txt");
        File.WriteAllText(path, "original");
        WriteExtendedAttribute(path, "sunder.test", "preserve-this-metadata");
        WriteExtendedAttribute(path, "sunder.second", "文-😀-\0-binary-value");
        using var expected = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        var originalAttributes = ReadExtendedAttributes(expected.Handle);
        Assert.NotEmpty(originalAttributes);
        using var slots = root.Platform.ReserveMutationSlots(root.Handle, 4, CancellationToken.None);
        using var temporary = Stage(root.Platform, root.Handle, slots, "SECRET");
        var failureInjected = false;
        void Checkpoint(LocalSecureFileSystemCheckpoint checkpoint)
        {
            if (failAfterPublication && checkpoint == LocalSecureFileSystemCheckpoint.AfterPublishedFileSync)
            {
                failureInjected = true;
                throw new LocalSecurePathException("Injected failure after extended-attribute transfer.");
            }
        }
        if (failAfterPublication)
            Assert.Throws<LocalSecurePathException>(() => Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint));
        else
            Publish(root.Platform, root.Handle, temporary, "target.txt", expected, slots, Checkpoint);
        Assert.Equal(failAfterPublication, failureInjected);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(failAfterPublication ? expected.Identity : temporary.Identity, final.Identity);
        Assert.Equal(failAfterPublication ? "original" : "SECRET", Read(final));
        Assert.Equal(originalAttributes, ReadExtendedAttributes(final.Handle));
        Assert.Equal(originalAttributes, ReadExtendedAttributes(expected.Handle));
        if (failAfterPublication)
            Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
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
        try
        {
            Assert.True(GetDescriptorSacl(descriptor, out var present, out var sacl, out _));
            Assert.True(present);
            Assert.Equal(0U, SetFileSecurity(file, 1 /* SE_FILE_OBJECT */, 0x10 /* LABEL_SECURITY_INFORMATION */,
                IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, sacl));
        }
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

    [PlatformTheory(TestPlatform.Windows, "Security and EA writes do not take the default-stream content lock and must be revalidated.")]
    [InlineData(false)]
    [InlineData(true)]
    public void MetadataChangedBeforePublication_RejectsStaleSnapshotAndRetainsTheConcurrentChange(bool changeSecurity)
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
            // WRITE_EA/WRITE_OWNER are not FILE_WRITE_DATA. Retain the exact
            // quarantined object while proving the final snapshot check matters.
            using var edit = LocalWindowsNative.Reopen(expected.Handle, LocalWindowsNative.GenericRead
                | (changeSecurity ? LocalWindowsNative.WriteOwner : 0x10U /* FILE_WRITE_EA */), LocalWindowsNative.ShareAll, path);
            if (changeSecurity) SetLowIntegrityLabel(edit);
            else WriteExtendedAttribute(edit, "sunder.concurrent", "RETAIN-CONCURRENT-METADATA");
            changed = true;
        }));
        Assert.True(changed);
        root.Platform.DeleteTemporaryFile(root.Handle, temporary, slots);
        using var final = root.Platform.OpenChild(root.Handle, "target.txt", root.Volume, false);
        Assert.Equal(expected.Identity, final.Identity);
        Assert.Equal("original", Read(final));
        if (changeSecurity)
        {
            using var security = LocalWindowsNative.CaptureSecurity(final.Handle, path);
            Assert.NotEmpty(security.AccessPolicyBytes);
        }
        else
            Assert.Contains(ReadExtendedAttributes(final.Handle), entry => entry.Contains("SUNDER.CONCURRENT", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(Directory.EnumerateFiles(_root).Select(ReadPath), text => text.Contains("SECRET", StringComparison.Ordinal));
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

    [DllImport("advapi32.dll", EntryPoint = "GetSecurityDescriptorSacl", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDescriptorSacl(IntPtr descriptor, [MarshalAs(UnmanagedType.Bool)] out bool present,
        out IntPtr sacl, [MarshalAs(UnmanagedType.Bool)] out bool defaulted);

    [DllImport("advapi32.dll", EntryPoint = "SetSecurityInfo")]
    private static extern uint SetFileSecurity(SafeFileHandle file, int type, uint parts, IntPtr owner, IntPtr group, IntPtr dacl, IntPtr sacl);

    [DllImport("kernel32.dll", EntryPoint = "LocalFree")]
    private static extern IntPtr FreeSecurityDescriptor(IntPtr descriptor);

    [DllImport("ntdll.dll")]
    private static extern int NtSetEaFile(SafeFileHandle file, out NativeCompletion completion, byte[] buffer, uint length);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryEaFile(SafeFileHandle file, out NativeCompletion completion, [Out] byte[] buffer, uint length,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry, IntPtr eaList, uint eaListLength, IntPtr eaIndex,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);
}
