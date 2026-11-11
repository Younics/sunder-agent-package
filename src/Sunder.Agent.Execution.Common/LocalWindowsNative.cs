using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

internal static class LocalWindowsNative
{
    private const string Kernel32 = "kernel32.dll";
    private const string Advapi32 = "advapi32.dll";
    private const string Ntdll = "ntdll.dll";

    public const uint GenericRead = 0x80000000;
    public const uint GenericWrite = 0x40000000;
    public const uint DeleteAccess = 0x00010000;
    public const uint ReadAttributes = 0x00000080;
    public const uint ListDirectory = 0x00000001;
    public const uint ReadControl = 0x00020000;
    public const uint WriteDac = 0x00040000;
    public const uint WriteOwner = 0x00080000;
    public const uint Synchronize = 0x00100000;
    public const uint ShareRead = 0x00000001;
    public const uint ShareWrite = 0x00000002;
    public const uint ShareDelete = 0x00000004;
    public const uint ShareAll = ShareRead | ShareWrite | ShareDelete;
    public const uint CreateNew = 1;
    public const uint OpenExisting = 3;
    public const uint BackupSemantics = 0x02000000;
    public const uint OpenReparsePoint = 0x00200000;
    public const uint AttributeDirectory = 0x00000010;
    public const uint AttributeReparsePoint = 0x00000400;
    public const uint FileTypeDisk = 0x0001;

    private const int FileDispositionInfoEx = 21;
    private const int FileRenameInfoEx = 22;
    private const int FileCaseSensitiveInfo = 23;
    private const int FileIdInfo = 18;
    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;
    private const int FileIdBothDirectoryInfo = 10;
    private const int FileIdBothDirectoryRestartInfo = 11;
    private const uint FileOpen = 1;
    private const uint FileCreate = 2;
    private const uint FileOpenReparsePoint = 0x00200000;
    private const uint FileSynchronousIoNonAlert = 0x00000020;
    private const uint FileOpenForBackupIntent = 0x00004000;
    private const uint FileDirectoryFile = 0x00000001;
    private const uint FileNonDirectoryFile = 0x00000040;
    private const uint ObjectCaseInsensitive = 0x00000040;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint CaseSensitiveDirectory = 0x00000001;
    private const uint OwnerSecurityInformation = 0x00000001;
    private const uint GroupSecurityInformation = 0x00000002;
    private const uint DaclSecurityInformation = 0x00000004;
    private const uint ProtectedDaclSecurityInformation = 0x80000000;
    private const uint UnprotectedDaclSecurityInformation = 0x20000000;
    private const ushort DaclProtected = 0x1000;
    private const int SecurityFileObject = 1;
    private const uint RenameReplaceIfExists = 0x00000001;
    private const uint RenamePosixSemantics = 0x00000002;
    private const uint DispositionDelete = 0x00000001;
    private const uint DispositionPosixSemantics = 0x00000002;
    private const uint DispositionIgnoreReadonly = 0x00000010;
    private const uint DuplicateSameAccess = 0x00000002;

    public static SafeFileHandle OpenHandle(
        string path,
        uint desiredAccess,
        uint creationDisposition,
        uint shareMode = ShareRead)
        => CreateFile(
            ToExtendedPath(path),
            desiredAccess,
            shareMode,
            IntPtr.Zero,
            creationDisposition,
            BackupSemantics | OpenReparsePoint,
            IntPtr.Zero);

    public static SafeFileHandle OpenRelative(
        SafeFileHandle parent,
        string name,
        uint desiredAccess,
        bool requireDirectory,
        bool create,
        bool caseSensitive,
        string displayPath,
        uint shareMode = ShareRead,
        IntPtr securityDescriptor = default)
    {
        var nameBuffer = Marshal.StringToHGlobalUni(name);
        var unicodeStringBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<UnicodeString>());
        var parentRetained = false;
        try
        {
            parent.DangerousAddRef(ref parentRetained);
            var nameBytes = checked(name.Length * sizeof(char));
            if (nameBytes > ushort.MaxValue - sizeof(char))
            {
                throw new LocalSecurePathException($"A Windows path component is too long: {displayPath}");
            }
            var unicodeString = new UnicodeString
            {
                Length = checked((ushort)nameBytes),
                MaximumLength = checked((ushort)(nameBytes + sizeof(char))),
                Buffer = nameBuffer,
            };
            Marshal.StructureToPtr(unicodeString, unicodeStringBuffer, fDeleteOld: false);
            var attributes = new ObjectAttributes
            {
                Length = Marshal.SizeOf<ObjectAttributes>(),
                RootDirectory = parent.DangerousGetHandle(),
                ObjectName = unicodeStringBuffer,
                Attributes = caseSensitive ? 0U : ObjectCaseInsensitive,
                SecurityDescriptor = securityDescriptor,
            };
            var options = FileOpenReparsePoint
                          | FileSynchronousIoNonAlert
                          | FileOpenForBackupIntent
                          | (requireDirectory ? FileDirectoryFile : 0U)
                          | (create && !requireDirectory ? FileNonDirectoryFile : 0U);
            var status = NtCreateFile(
                out var nativeHandle,
                desiredAccess | Synchronize,
                ref attributes,
                out _,
                IntPtr.Zero,
                FileAttributeNormal,
                shareMode,
                create ? FileCreate : FileOpen,
                options,
                IntPtr.Zero,
                0);
            if (status < 0)
            {
                var error = checked((int)RtlNtStatusToDosError(status));
                if (error is 2 or 3)
                {
                    throw new LocalSecurePathNotFoundException(displayPath);
                }
                if (error is 80 or 183)
                {
                    throw new LocalSecureFileAlreadyExistsException(displayPath);
                }
                throw Error("NtCreateFile", displayPath, error);
            }
            return new SafeFileHandle(nativeHandle, ownsHandle: true);
        }
        finally
        {
            if (parentRetained) parent.DangerousRelease();
            Marshal.FreeHGlobal(unicodeStringBuffer);
            Marshal.FreeHGlobal(nameBuffer);
        }
    }

    public static SafeFileHandle Reopen(SafeFileHandle source, uint access, uint sharing, string path)
    {
        var reopened = ReOpenFile(source, access, sharing, BackupSemantics | OpenReparsePoint);
        if (!reopened.IsInvalid) return reopened;
        var error = Marshal.GetLastPInvokeError();
        reopened.Dispose();
        throw Error("ReOpenFile", path, error);
    }

    public static bool IsUnlinked(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleExStandard(handle, FileStandardInfo, out var information,
                (uint)Marshal.SizeOf<FileStandardInformation>()))
            throw Error("GetFileInformationByHandleEx(FileStandardInfo)", path);
        return information.NumberOfLinks == 0;
    }

    public static SafeFileHandle CreatePrivateFile(SafeFileHandle parent, string name, bool caseSensitive, string path)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw new LocalSecurePathException("A Windows temporary file requires a user SID.");
        // The protected DACL is supplied to NtCreateFile, never applied after bytes
        // have been exposed under an inherited, possibly public, descriptor.
        // Retain the token's normal default owner/group (including an owner-capable
        // Administrators group for elevated callers). Only the DACL is restricted;
        // forcing a different owner here would prevent preserving ordinary files.
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor($"D:P(A;;FA;;;{sid})", 1,
                out var descriptor, out _))
            throw Error("ConvertStringSecurityDescriptorToSecurityDescriptor", path);
        try
        {
            return OpenRelative(parent, name, GenericRead | GenericWrite | DeleteAccess | ReadControl | WriteDac,
                requireDirectory: false, create: true, caseSensitive, path, ShareRead, descriptor);
        }
        finally { _ = LocalFree(descriptor); }
    }

    public static WindowsMetadata ReadMetadata(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandle(handle, out var information))
        {
            throw Error("GetFileInformationByHandle", path);
        }
        if ((information.FileAttributes & AttributeReparsePoint) != 0)
        {
            throw new LocalSecureSymbolicLinkException(path);
        }
        if (GetFileType(handle) != FileTypeDisk)
        {
            throw new LocalSecurePathException($"Path is not a disk file or directory: {path}");
        }
        if (!GetFileInformationByHandleEx(
                handle,
                FileIdInfo,
                out var identity,
                (uint)Marshal.SizeOf<ByHandleFileIdInformation>()))
        {
            throw Error("GetFileInformationByHandleEx(FileIdInfo)", path);
        }

        var kind = (information.FileAttributes & AttributeDirectory) != 0
            ? LocalSecureNodeKind.Directory
            : LocalSecureNodeKind.RegularFile;
        return new WindowsMetadata(
            identity.VolumeSerialNumber,
            identity.FileIdLow,
            identity.FileIdHigh,
            kind,
            checked((long)(((ulong)information.FileSizeHigh << 32) | information.FileSizeLow)),
            information.NumberOfLinks);
    }

    public static IEnumerable<string> EnumerateNames(SafeFileHandle directory, string directoryPath)
    {
        const int bufferSize = 64 * 1024;
        const int fileNameOffset = 104;
        var buffer = Marshal.AllocHGlobal(bufferSize);
        try
        {
            var restart = true;
            while (true)
            {
                if (!GetFileInformationByHandleExBuffer(
                        directory,
                        restart ? FileIdBothDirectoryRestartInfo : FileIdBothDirectoryInfo,
                        buffer,
                        bufferSize))
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error == 18)
                    {
                        yield break;
                    }
                    throw Error("GetFileInformationByHandleEx(FileIdBothDirectoryInfo)", directoryPath, error);
                }
                restart = false;
                var offset = 0;
                while (true)
                {
                    if (offset < 0 || offset + fileNameOffset > bufferSize)
                    {
                        throw new LocalSecurePathException($"Windows directory enumeration returned invalid data for '{directoryPath}'.");
                    }
                    var entry = buffer + offset;
                    var nextOffset = Marshal.ReadInt32(entry, 0);
                    var nameLength = Marshal.ReadInt32(entry, 60);
                    if (nameLength < 0
                        || (nameLength & 1) != 0
                        || nameLength > bufferSize - offset - fileNameOffset)
                    {
                        throw new LocalSecurePathException($"Windows directory enumeration returned an invalid name for '{directoryPath}'.");
                    }
                    var name = Marshal.PtrToStringUni(entry + fileNameOffset, nameLength / sizeof(char))
                               ?? throw new LocalSecurePathException($"Windows directory enumeration returned an invalid name for '{directoryPath}'.");
                    if (name is not "." and not "..")
                    {
                        yield return name;
                    }
                    if (nextOffset == 0)
                    {
                        break;
                    }
                    if (nextOffset < fileNameOffset || nextOffset > bufferSize - offset)
                    {
                        throw new LocalSecurePathException($"Windows directory enumeration returned an invalid offset for '{directoryPath}'.");
                    }
                    offset += nextOffset;
                }
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static bool IsCaseSensitiveDirectory(SafeFileHandle directory, string path)
    {
        if (!GetFileInformationByHandleExCaseSensitive(
                directory,
                FileCaseSensitiveInfo,
                out var information,
                (uint)Marshal.SizeOf<FileCaseSensitiveInformation>()))
        {
            throw Error("GetFileInformationByHandleEx(FileCaseSensitiveInfo)", path);
        }
        return (information.Flags & CaseSensitiveDirectory) != 0;
    }

    public static void CopyBasicMetadata(
        SafeFileHandle source,
        SafeFileHandle destination,
        string sourcePath,
        string destinationPath)
    {
        if (!GetFileInformationByHandleExBasic(
                source,
                FileBasicInfo,
                out var basic,
                (uint)Marshal.SizeOf<FileBasicInformation>()))
        {
            throw Error("GetFileInformationByHandleEx(FileBasicInfo)", sourcePath);
        }
        // These attributes imply data/metadata which a default-stream rewrite
        // cannot faithfully reproduce. Refuse rather than silently discard it.
        const uint unsupportedAttributes = 0x00000200 | 0x00000800 | 0x00001000 | 0x00004000 | 0x00008000;
        if ((basic.FileAttributes & unsupportedAttributes) != 0)
            throw new PlatformNotSupportedException($"Strict Windows writes cannot preserve the storage attributes of '{sourcePath}'.");
        RequireDefaultStreamOnly(source, sourcePath);
        var basicBuffer = Marshal.AllocHGlobal(Marshal.SizeOf<FileBasicInformation>());
        try
        {
            Marshal.StructureToPtr(basic, basicBuffer, fDeleteOld: false);
            if (!SetFileInformationByHandle(
                    destination,
                    FileBasicInfo,
                    basicBuffer,
                    (uint)Marshal.SizeOf<FileBasicInformation>()))
            {
                throw Error("SetFileInformationByHandle(FileBasicInfo)", destinationPath);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(basicBuffer);
        }

    }

    public static FileSecuritySnapshot CaptureSecurity(SafeFileHandle source, string path)
    {
        var result = GetSecurityInfo(
            source,
            SecurityFileObject,
            OwnerSecurityInformation | GroupSecurityInformation | DaclSecurityInformation,
            out var owner,
            out var group,
            out var dacl,
            out _,
            out var securityDescriptor);
        if (result != 0)
        {
            throw Error("GetSecurityInfo", path, checked((int)result));
        }
        try
        {
            if (!GetSecurityDescriptorControl(securityDescriptor, out var control, out _))
                throw Error("GetSecurityDescriptorControl", path);
            return new FileSecuritySnapshot(securityDescriptor, owner, group, dacl, (control & DaclProtected) != 0);
        }
        catch
        {
            _ = LocalFree(securityDescriptor);
            throw;
        }
    }

    internal sealed class FileSecuritySnapshot(IntPtr descriptor, IntPtr owner, IntPtr group, IntPtr dacl, bool protectedDacl) : IDisposable
    {
        private IntPtr _descriptor = descriptor;
        public bool ProtectedDacl { get; } = protectedDacl;
        public byte[] DaclBytes
        {
            get
            {
                ObjectDisposedException.ThrowIf(_descriptor == IntPtr.Zero, this);
                if (dacl == IntPtr.Zero) return [];
                var length = unchecked((ushort)Marshal.ReadInt16(dacl, 2));
                if (length < 8) throw new LocalSecurePathException("A Windows file has an invalid DACL.");
                var bytes = new byte[length];
                Marshal.Copy(dacl, bytes, 0, length);
                return bytes;
            }
        }

        public void RequireSameOwner(SafeFileHandle destination, string path)
        {
            ObjectDisposedException.ThrowIf(_descriptor == IntPtr.Zero, this);
            using var current = CaptureSecurity(destination, path);
            if (!SidsEqual(owner, current.Owner) || !SidsEqual(group, current.Group))
                throw new PlatformNotSupportedException($"Strict Windows writes cannot preserve the owner or primary group of '{path}'.");
        }

        private IntPtr Owner => owner;
        private IntPtr Group => group;

        public void ApplyDacl(SafeFileHandle destination, string path)
        {
            ObjectDisposedException.ThrowIf(_descriptor == IntPtr.Zero, this);
            RequireSameOwner(destination, path);
            var result = SetSecurityInfo(destination, SecurityFileObject,
                DaclSecurityInformation | (ProtectedDacl ? ProtectedDaclSecurityInformation : UnprotectedDaclSecurityInformation),
                IntPtr.Zero, IntPtr.Zero, dacl, IntPtr.Zero);
            if (result != 0) throw Error("SetSecurityInfo", path, checked((int)result));
            using var applied = CaptureSecurity(destination, path);
            if (applied.ProtectedDacl != ProtectedDacl || !applied.DaclBytes.AsSpan().SequenceEqual(DaclBytes))
                throw new LocalSecurePathException($"The Windows DACL could not be preserved exactly: {path}");
        }

        public void Dispose()
        {
            var allocated = Interlocked.Exchange(ref _descriptor, IntPtr.Zero);
            if (allocated != IntPtr.Zero) _ = LocalFree(allocated);
        }
    }

    private static void RequireDefaultStreamOnly(SafeFileHandle source, string path)
    {
        const int size = 64 * 1024;
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!GetFileInformationByHandleExBuffer(source, 7 /* FileStreamInfo */, buffer, size))
                throw Error("GetFileInformationByHandleEx(FileStreamInfo)", path);
            var length = Marshal.ReadInt32(buffer, 4);
            if (Marshal.ReadInt32(buffer) != 0 || length != "::$DATA".Length * sizeof(char)
                || Marshal.PtrToStringUni(buffer + 24, length / sizeof(char)) != "::$DATA")
                throw new PlatformNotSupportedException($"Strict Windows writes cannot preserve alternate data streams of '{path}'.");
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    public static SafeFileHandle Duplicate(SafeFileHandle source)
    {
        var process = GetCurrentProcess();
        if (!DuplicateHandle(
                process,
                source,
                process,
                out var duplicate,
                0,
                inheritHandle: false,
                DuplicateSameAccess))
        {
            throw Error("DuplicateHandle", "secure file handle");
        }
        return duplicate;
    }

    public static void Flush(SafeFileHandle handle, string path)
    {
        // Normal flags include metadata and the storage cache; no DATA_ONLY or
        // NO_SYNC fallback is permitted. The caller supplies write/append access.
        var status = NtFlushBuffersFileEx(handle, 0, IntPtr.Zero, 0, out _);
        if (status < 0) throw Error("NtFlushBuffersFileEx", path, checked((int)RtlNtStatusToDosError(status)));
    }

    public static void RenameNoReplace(
        SafeFileHandle file,
        SafeFileHandle parent,
        string targetName,
        string path)
    {
        var nameLength = checked(targetName.Length * sizeof(char));
        var rootOffset = IntPtr.Size == 8 ? 8 : 4;
        var lengthOffset = rootOffset + IntPtr.Size;
        var nameOffset = lengthOffset + sizeof(uint);
        var bufferSize = checked((nameOffset + nameLength + sizeof(char) + IntPtr.Size - 1) & -IntPtr.Size);
        var buffer = Marshal.AllocHGlobal(bufferSize);
        var parentRetained = false;
        try
        {
            parent.DangerousAddRef(ref parentRetained);
            Marshal.Copy(new byte[bufferSize], 0, buffer, bufferSize);
            Marshal.WriteInt32(buffer, 0); // Never replace an entry selected by name.
            Marshal.WriteIntPtr(buffer, rootOffset, parent.DangerousGetHandle());
            Marshal.WriteInt32(buffer, lengthOffset, nameLength);
            // NT names are UTF-16 code units. Encoding.Unicode's replacement
            // fallback would silently select a different name for a lone surrogate.
            Marshal.Copy(targetName.ToCharArray(), 0, buffer + nameOffset, targetName.Length);
            // Native FileRenameInformationEx (65) supports RootDirectory. Do not
            // rely on Win32's reserved FILE_RENAME_INFO.RootDirectory field.
            var status = NtSetInformationFile(file, out _, buffer, (uint)bufferSize, 65);
            if (status < 0)
            {
                var error = checked((int)RtlNtStatusToDosError(status));
                if (error is 80 or 183)
                {
                    throw new LocalSecureFileAlreadyExistsException(path);
                }
                if (error is 1 or 50 or 87)
                {
                    throw new PlatformNotSupportedException(
                        "The Windows filesystem does not support handle-relative atomic rename required by strict Local writes.",
                        new Win32Exception(error));
                }
                throw Error("NtSetInformationFile(FileRenameInformationEx)", path, error);
            }
        }
        finally
        {
            if (parentRetained) parent.DangerousRelease();
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static void Delete(SafeFileHandle handle, string path)
    {
        var buffer = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(
                buffer,
                unchecked((int)(DispositionDelete | DispositionPosixSemantics | DispositionIgnoreReadonly)));
            if (!SetFileInformationByHandle(handle, FileDispositionInfoEx, buffer, sizeof(uint)))
            {
                var error = Marshal.GetLastPInvokeError();
                if (error is 1 or 50 or 87)
                {
                    throw new PlatformNotSupportedException(
                        "The Windows filesystem does not support handle disposition required by strict Local deletes.",
                        new Win32Exception(error));
                }
                throw Error("SetFileInformationByHandle(FileDispositionInfoEx)", path, error);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static Exception Error(string operation, string path)
        => Error(operation, path, Marshal.GetLastPInvokeError());

    private static bool SidsEqual(IntPtr first, IntPtr second)
        => first == IntPtr.Zero || second == IntPtr.Zero
            ? first == second
            : EqualSid(first, second);

    public static Exception Error(string operation, string path, int error)
        => new LocalSecurePathException(
            $"Secure filesystem {operation} failed for '{path}': {new Win32Exception(error).Message}");

    public static string ToExtendedPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        if (fullPath.StartsWith("\\\\?\\", StringComparison.Ordinal))
        {
            return fullPath;
        }
        return fullPath.StartsWith("\\\\", StringComparison.Ordinal)
            ? "\\\\?\\UNC\\" + fullPath[2..]
            : "\\\\?\\" + fullPath;
    }

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
    private struct ByHandleFileIdInformation
    {
        public ulong VolumeSerialNumber;
        public ulong FileIdLow;
        public ulong FileIdHigh;
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

    [StructLayout(LayoutKind.Sequential)]
    private struct FileCaseSensitiveInformation
    {
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInformation
    {
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public long ChangeTime;
        public uint FileAttributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileStandardInformation
    {
        public long AllocationSize;
        public long EndOfFile;
        public uint NumberOfLinks;
        [MarshalAs(UnmanagedType.U1)] public bool DeletePending;
        [MarshalAs(UnmanagedType.U1)] public bool Directory;
    }

    [DllImport(Kernel32, EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
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
        SafeFileHandle file,
        out ByHandleFileInformation information);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(
        SafeFileHandle file,
        int fileInformationClass,
        out ByHandleFileIdInformation fileInformation,
        uint bufferSize);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleExBuffer(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        int bufferSize);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleExCaseSensitive(
        SafeFileHandle file,
        int fileInformationClass,
        out FileCaseSensitiveInformation fileInformation,
        uint bufferSize);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleExBasic(
        SafeFileHandle file,
        int fileInformationClass,
        out FileBasicInformation fileInformation,
        uint bufferSize);

    [DllImport(Kernel32, EntryPoint = "GetFileInformationByHandleEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleExStandard(
        SafeFileHandle file, int fileInformationClass, out FileStandardInformation information, uint bufferSize);

    [DllImport(Kernel32, SetLastError = true)]
    private static extern SafeFileHandle ReOpenFile(SafeFileHandle original, uint desiredAccess, uint shareMode, uint flags);

    [DllImport(Advapi32, EntryPoint = "ConvertStringSecurityDescriptorToSecurityDescriptorW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(
        string descriptor, uint revision, out IntPtr securityDescriptor, out uint size);

    [DllImport(Kernel32)]
    private static extern uint GetFileType(SafeFileHandle file);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle file);

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle file,
        int fileInformationClass,
        IntPtr fileInformation,
        uint bufferSize);

    [DllImport(Kernel32)]
    private static extern IntPtr GetCurrentProcess();

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DuplicateHandle(
        IntPtr sourceProcess,
        SafeFileHandle sourceHandle,
        IntPtr targetProcess,
        out SafeFileHandle targetHandle,
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint options);

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
    private static extern int NtFlushBuffersFileEx(SafeFileHandle handle, uint flags, IntPtr parameters, uint parametersSize,
        out IoStatusBlock ioStatusBlock);

    [DllImport(Ntdll)]
    private static extern int NtSetInformationFile(SafeFileHandle handle, out IoStatusBlock ioStatusBlock,
        IntPtr information, uint length, int informationClass);

    [DllImport(Ntdll)]
    private static extern uint RtlNtStatusToDosError(int status);

    [DllImport(Advapi32, SetLastError = true)]
    private static extern uint GetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        out IntPtr owner,
        out IntPtr group,
        out IntPtr dacl,
        out IntPtr sacl,
        out IntPtr securityDescriptor);

    [DllImport(Advapi32, SetLastError = true)]
    private static extern uint SetSecurityInfo(
        SafeFileHandle handle,
        int objectType,
        uint securityInformation,
        IntPtr owner,
        IntPtr group,
        IntPtr dacl,
        IntPtr sacl);

    [DllImport(Advapi32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSecurityDescriptorControl(IntPtr descriptor, out ushort control, out uint revision);

    [DllImport(Advapi32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EqualSid(IntPtr firstSid, IntPtr secondSid);

    [DllImport(Kernel32)]
    private static extern IntPtr LocalFree(IntPtr memory);
}

internal readonly record struct WindowsMetadata(
    ulong Volume,
    ulong FileId,
    ulong FileIdHigh,
    LocalSecureNodeKind Kind,
    long Length,
    uint LinkCount);
