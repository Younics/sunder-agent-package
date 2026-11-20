using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sunder.Agent.Execution.Common;

internal static partial class LocalUnixNative
{
    private const string LibC = "libc";
    private const int StatBufferSize = 256;
    private const uint FileTypeMask = 0xf000;
    private const uint DirectoryType = 0x4000;
    private const uint RegularFileType = 0x8000;

    public const int ReadOnly = 0;
    public const int WriteOnly = 1;
    public const int ReadWrite = 2;

    public static int DirectoryFlag => OperatingSystem.IsMacOS()
        ? 0x00100000
        : GetLinuxDirectoryFlag(RuntimeInformation.ProcessArchitecture);

    public static int NoFollowFlag => OperatingSystem.IsMacOS()
        ? 0x00000100
        : GetLinuxNoFollowFlag(RuntimeInformation.ProcessArchitecture);

    public static int CloseOnExecFlag => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;

    public static int NonBlockingFlag => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;

    public static int CreateFlag => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;

    public static int ExclusiveFlag => OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;

    private static bool UseDarwinInode64
        => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.X64;

    public static int Open(string path, int flags, uint mode = 0)
        => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAppleArm64(path, flags, 0, 0, 0, 0, 0, 0, mode)
            : OpenNative(path, flags, mode);

    public static int OpenAt(
        int directoryFileDescriptor,
        string name,
        int flags,
        uint mode = 0,
        bool invalidArgumentIsOperationError = false)
    {
        if (OperatingSystem.IsLinux())
        {
            return OpenAt2(
                directoryFileDescriptor,
                name,
                flags,
                mode,
                invalidArgumentIsOperationError);
        }

        return RuntimeInformation.ProcessArchitecture == Architecture.Arm64 && OperatingSystem.IsMacOS()
            ? OpenAtAppleArm64(directoryFileDescriptor, name, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtNative(directoryFileDescriptor, name, flags, mode);
    }

    public static UnixMetadata ReadMetadata(int fileDescriptor)
    {
        var buffer = Marshal.AllocHGlobal(StatBufferSize);
        try
        {
            var result = UseDarwinInode64 ? FStatDarwinInode64(fileDescriptor, buffer) : FStat(fileDescriptor, buffer);
            if (result != 0)
            {
                throw Error("fstat");
            }

            if (OperatingSystem.IsMacOS())
            {
                EnsureSupportedArchitecture();
                return new UnixMetadata(
                    unchecked((ulong)(uint)Marshal.ReadInt32(buffer, 0)),
                    unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                    unchecked((ushort)Marshal.ReadInt16(buffer, 4)),
                    Marshal.ReadInt64(buffer, 96),
                    unchecked((ushort)Marshal.ReadInt16(buffer, 6)),
                    unchecked((uint)Marshal.ReadInt32(buffer, 16)),
                    unchecked((uint)Marshal.ReadInt32(buffer, 20)));
            }

            return ReadLinuxMetadata(buffer, RuntimeInformation.ProcessArchitecture);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static LocalSecureNodeKind GetNodeKind(UnixMetadata metadata, string path)
        => (metadata.Mode & FileTypeMask) switch
        {
            DirectoryType => LocalSecureNodeKind.Directory,
            RegularFileType => LocalSecureNodeKind.RegularFile,
            _ => throw new LocalSecurePathException($"Path is not a regular file or directory: {path}"),
        };

    public static IEnumerable<string> EnumerateNames(int directoryFileDescriptor)
    {
        var duplicate = OpenAt(
            directoryFileDescriptor,
            ".",
            ReadOnly | DirectoryFlag | NoFollowFlag | CloseOnExecFlag);
        if (duplicate < 0)
        {
            throw Error("openat directory duplicate");
        }

        var directory = UseDarwinInode64 ? FdOpenDirDarwinInode64(duplicate) : FdOpenDir(duplicate);
        if (directory == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = Close(duplicate);
            throw Error("fdopendir", error);
        }

        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = UseDarwinInode64 ? ReadDirDarwinInode64(directory) : ReadDir(directory);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                    {
                        throw Error("readdir", error);
                    }
                    yield break;
                }

                var nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
                var recordLength = unchecked((ushort)Marshal.ReadInt16(entry, 16));
                if (recordLength <= nameOffset)
                {
                    throw new LocalSecurePathException("A directory entry reported an invalid native record length.");
                }
                var maximumNameBytes = recordLength - nameOffset;
                if (OperatingSystem.IsMacOS())
                {
                    var declaredNameBytes = unchecked((ushort)Marshal.ReadInt16(entry, 18));
                    if (declaredNameBytes >= maximumNameBytes)
                    {
                        throw new LocalSecurePathException("A directory entry reported an invalid native name length.");
                    }
                    maximumNameBytes = declaredNameBytes + 1;
                }
                var name = ReadNullTerminatedUtf8(entry + nameOffset, maximumNameBytes);
                if (name is not "." and not "..")
                {
                    yield return name;
                }
            }
        }
        finally
        {
            _ = CloseDir(directory);
        }
    }

    public static void MakeDirectory(int parentFileDescriptor, string name)
    {
        if (MkdirAt(parentFileDescriptor, name, Convert.ToUInt32("700", 8)) != 0)
        {
            throw Error("mkdirat");
        }
    }

    public static void RenameNoReplace(
        int oldParentFileDescriptor,
        string oldName,
        int newParentFileDescriptor,
        string newName)
    {
        var result = OperatingSystem.IsMacOS()
            ? RenameAtExclusive(oldParentFileDescriptor, oldName, newParentFileDescriptor, newName)
            : RenameAt2(oldParentFileDescriptor, oldName, newParentFileDescriptor, newName, LinuxRenameNoReplace);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 17)
            {
                throw new LocalSecureFileAlreadyExistsException(newName);
            }
            if (error == 2)
            {
                throw new LocalSecurePathNotFoundException(oldName);
            }
            if (error is 22 or 38 or 45 or 95)
            {
                throw new PlatformNotSupportedException(
                    "The filesystem does not support exclusive rename required by strict structured mutations.",
                    new Win32Exception(error));
            }
            throw Error("exclusive rename", error);
        }
    }

    public static int Duplicate(int fileDescriptor)
    {
        var duplicate = Dup(fileDescriptor);
        if (duplicate < 0)
        {
            throw Error("duplicate descriptor");
        }
        if (Fcntl(duplicate, 2, 1) < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = Close(duplicate);
            throw Error("mark duplicate descriptor close-on-exec", error);
        }
        return duplicate;
    }

    public static void Sync(int fileDescriptor)
    {
        if (FSync(fileDescriptor) != 0)
        {
            throw Error("fsync");
        }
    }

    public static uint GetEffectiveUserId()
        => OperatingSystem.IsWindows()
            ? throw new PlatformNotSupportedException("POSIX user identity is unavailable on Windows.")
            : GetEffectiveUserIdNative();

    public static uint GetEffectiveGroupId()
        => OperatingSystem.IsWindows()
            ? throw new PlatformNotSupportedException("POSIX group identity is unavailable on Windows.")
            : GetEffectiveGroupIdNative();

    public static Exception Error(string operation)
        => Error(operation, Marshal.GetLastPInvokeError());

    public static Exception Error(string operation, int error)
        => new LocalSecurePathException($"Secure filesystem {operation} failed: {new Win32Exception(error).Message}");

    private static string ReadNullTerminatedUtf8(IntPtr pointer, int maximumBytes)
    {
        var length = 0;
        while (length < maximumBytes && Marshal.ReadByte(pointer, length) != 0)
        {
            length++;
        }
        if (length == maximumBytes)
        {
            throw new LocalSecurePathException("A directory entry name was not terminated within the platform bound.");
        }

        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        try
        {
            return new System.Text.UTF8Encoding(false, true).GetString(bytes);
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            throw new LocalSecurePathException("A directory entry name was not valid UTF-8.", ex);
        }
    }

    private static void EnsureSupportedArchitecture()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64))
        {
            throw UnsupportedArchitecture();
        }
    }

    private static PlatformNotSupportedException UnsupportedArchitecture()
        => new($"Strict Local Unix filesystem operations do not support {RuntimeInformation.ProcessArchitecture} ABI layouts.");

    [DllImport(LibC, EntryPoint = "open", SetLastError = true)]
    private static extern int OpenNative(
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);

    [DllImport(LibC, EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtNative(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags,
        uint mode);

    [DllImport(LibC, EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, IntPtr buffer);

    [DllImport(LibC, EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    [DllImport(LibC, EntryPoint = "fdopendir", SetLastError = true)]
    private static extern IntPtr FdOpenDir(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "readdir", SetLastError = true)]
    private static extern IntPtr ReadDir(IntPtr directory);

    [DllImport(LibC, EntryPoint = "closedir", SetLastError = true)]
    private static extern int CloseDir(IntPtr directory);

    [DllImport(LibC, EntryPoint = "close", SetLastError = true)]
    private static extern int Close(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "dup", SetLastError = true)]
    private static extern int Dup(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "fsync", SetLastError = true)]
    private static extern int FSync(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "fcntl", SetLastError = true)]
    private static extern int Fcntl(int fileDescriptor, int command, int argument);

    [DllImport(LibC, EntryPoint = "geteuid")]
    private static extern uint GetEffectiveUserIdNative();

    [DllImport(LibC, EntryPoint = "getegid")]
    private static extern uint GetEffectiveGroupIdNative();
}

internal readonly record struct UnixMetadata(
    ulong Device,
    ulong Inode,
    uint Mode,
    long Length,
    ulong LinkCount,
    uint UserId,
    uint GroupId);
