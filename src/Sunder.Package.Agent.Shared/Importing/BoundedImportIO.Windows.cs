using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Package.Agent.Shared.Importing;

internal static partial class BoundedImportIO
{
    private const string Kernel32 = "kernel32.dll";
    private const string Ntdll = "ntdll.dll";
    private const uint WindowsGenericRead = 0x80000000;
    private const uint WindowsGenericWrite = 0x40000000;
    private const uint WindowsReadAttributes = 0x00000080;
    private const uint WindowsShareRead = 0x00000001;
    private const uint WindowsShareWrite = 0x00000002;
    private const uint WindowsShareDelete = 0x00000004;
    private const uint WindowsShareAll = WindowsShareRead | WindowsShareWrite | WindowsShareDelete;
    private const uint WindowsOpenExisting = 3;
    private const uint WindowsBackupSemantics = 0x02000000;
    private const uint WindowsOpenReparsePoint = 0x00200000;
    private const uint WindowsSynchronize = 0x00100000;
    private const uint WindowsFileOpen = 1;
    private const uint WindowsFileCreate = 2;
    private const uint WindowsFileOpenIf = 3;
    private const uint WindowsFileOpenReparsePoint = 0x00200000;
    private const uint WindowsFileSynchronousIoNonAlert = 0x00000020;
    private const uint WindowsFileOpenForBackupIntent = 0x00004000;
    private const uint WindowsFileDirectoryFile = 0x00000001;
    private const uint WindowsFileNonDirectoryFile = 0x00000040;
    private const uint WindowsObjectCaseInsensitive = 0x00000040;
    private const uint WindowsFileAttributeNormal = 0x00000080;

    private static SafeFileHandle OpenWindowsRoot(string rootPath)
    {
        var handle = CreateFile(rootPath, WindowsGenericRead, WindowsShareAll, IntPtr.Zero,
            WindowsOpenExisting, WindowsBackupSemantics | WindowsOpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            handle.Dispose();
            ThrowOpenError(rootPath, error);
        }
        return handle;
    }

    private static SafeFileHandle OpenWindowsRelative(SafeFileHandle parent, string name, uint desiredAccess,
        ImportNodeKind? requiredKind, uint creationDisposition, uint shareMode, string displayPath)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        try
        {
            var nameBytes = checked(name.Length * sizeof(char));
            if (nameBytes > ushort.MaxValue - sizeof(char))
                throw new InvalidDataException($"Import path component is too long: '{displayPath}'.");
            Marshal.StructureToPtr(new UnicodeString
            {
                Length = checked((ushort)nameBytes),
                MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                Buffer = nameBuffer,
            }, unicodeStringBuffer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = WindowsObjectCaseInsensitive,
            };
            var options = WindowsFileOpenReparsePoint | WindowsFileSynchronousIoNonAlert | WindowsFileOpenForBackupIntent
                | (requiredKind == ImportNodeKind.Directory ? WindowsFileDirectoryFile : 0U)
                | (requiredKind == ImportNodeKind.RegularFile ? WindowsFileNonDirectoryFile : 0U);
            var status = NtCreateFile(out var nativeHandle, desiredAccess | WindowsSynchronize, ref attributes,
                out _, IntPtr.Zero, WindowsFileAttributeNormal, shareMode, creationDisposition, options, IntPtr.Zero, 0);
            if (status < 0)
            {
                var error = checked((int)RtlNtStatusToDosError(status));
                if (creationDisposition == WindowsFileCreate && error is 80 or 183)
                    throw new IOException($"Import staging target already exists: '{displayPath}'.");
                ThrowOpenError(displayPath, error);
            }
            return new SafeFileHandle(nativeHandle, ownsHandle: true);
        }
        finally
        {
            Marshal.FreeHGlobal(unicodeStringBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    private static IEnumerable<string> EnumerateWindowsDirectoryNames(SafeFileHandle directory, string directoryPath)
    {
        const int bufferSize = 64 * 1024;
        const int fileNameOffset = 104;
        const int fileIdBothDirectoryInfo = 10;
        const int fileIdBothDirectoryRestartInfo = 11;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var restart = true;
            while (true)
            {
                if (!GetFileInformationByHandleExBuffer(directory,
                        restart ? fileIdBothDirectoryRestartInfo : fileIdBothDirectoryInfo, buffer, bufferSize))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 18) yield break;
                    throw new IOException($"Could not enumerate import directory '{directoryPath}'.", new Win32Exception(error));
                }
                restart = false;
                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset + fileNameOffset > bufferSize)
                        throw new InvalidDataException($"Import directory '{directoryPath}' returned invalid native data.");
                    var entry = buffer + offset;
                    var nextOffset = Marshal.ReadInt32(entry, 0);
                    var nameLength = Marshal.ReadInt32(entry, 60);
                    if (nameLength < 0 || (nameLength & 1) != 0 || nameLength > bufferSize - offset - fileNameOffset)
                        throw new InvalidDataException($"Import directory '{directoryPath}' returned an invalid native name.");
                    var name = Marshal.PtrToStringUni(entry + fileNameOffset, nameLength / sizeof(char))
                        ?? throw new InvalidDataException($"Import directory '{directoryPath}' returned an invalid native name.");
                    if (name is not "." and not "..") yield return name;
                    if (nextOffset == 0) break;
                    if (nextOffset < fileNameOffset || nextOffset > bufferSize - offset)
                        throw new InvalidDataException($"Import directory '{directoryPath}' returned an invalid native offset.");
                    offset += nextOffset;
                }
            }
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ImportNodeIdentity ReadWindowsIdentity(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException($"Could not inspect import source '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        if ((information.FileAttributes & 0x00000400) != 0)
            throw new InvalidDataException($"Import source contains a symbolic link or reparse point: '{path}'.");
        if (GetFileType(handle) != 0x0001)
            throw new InvalidDataException($"Import source is not a disk file or directory: '{path}'.");
        var kind = (information.FileAttributes & 0x00000010) != 0 ? ImportNodeKind.Directory : ImportNodeKind.RegularFile;
        return new ImportNodeIdentity(information.VolumeSerialNumber, information.FileIndexLow, information.FileIndexHigh,
            kind, checked((long)(((ulong)information.FileSizeHigh << 32) | information.FileSizeLow)), information.NumberOfLinks,
            FileTimeToLong(information.LastWriteTime), 0, FileTimeToLong(information.CreationTime), 0);
    }

    private static long FileTimeToLong(System.Runtime.InteropServices.ComTypes.FILETIME value)
        => ((long)value.dwHighDateTime << 32) | (uint)value.dwLowDateTime;

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public int Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport(Kernel32, CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle handle,
        out ByHandleFileInformation information);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleExBuffer(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport(Kernel32, SetLastError = true)]
    private static extern uint GetFileType(SafeFileHandle handle);

    [DllImport(Ntdll)]
    private static extern int NtCreateFile(
        out IntPtr fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        IntPtr allocationSize,
        uint fileAttributes,
        uint shareAccess,
        uint createDisposition,
        uint createOptions,
        IntPtr eaBuffer,
        uint eaLength);

    [DllImport(Ntdll)]
    private static extern uint RtlNtStatusToDosError(int status);
}
