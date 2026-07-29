using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Sunder.Agent.Execution.Common;

internal static class LocalUnixNative
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

    public static int RemoveDirectoryFlag => OperatingSystem.IsMacOS() ? 0x00000080 : 0x00000200;

    public static int TemporaryFileFlag => (1 << 22) | DirectoryFlag;

    private const int AtCurrentWorkingDirectory = -100;
    private const int AtEmptyPath = 0x1000;
    private const int AtSymlinkFollow = 0x400;
    private const uint DarwinRenameExclusive = 0x00000004;
    private const uint DarwinCloneNoFollow = 0x0001;
    private const uint LinuxRenameNoReplace = 0x00000001;
    private static readonly AsyncLocal<Func<LocalUnixAnonymousPublishMethod, int?>?> AnonymousPublishErrorOverride = new();
    private static readonly AsyncLocal<int?> AnonymousTemporaryFileOpenErrorOverride = new();

    public static int Open(string path, int flags, uint mode = 0)
        => OpenNative(path, flags, mode);

    public static int OpenAt(
        int directoryFileDescriptor,
        string name,
        int flags,
        uint mode = 0,
        bool invalidArgumentIsOperationError = false)
    {
        if (OperatingSystem.IsLinux())
        {
            if ((flags & TemporaryFileFlag) == TemporaryFileFlag
                && AnonymousTemporaryFileOpenErrorOverride.Value is { } forcedError)
            {
                Marshal.SetLastPInvokeError(forcedError);
                return -1;
            }
            return OpenAt2(
                directoryFileDescriptor,
                name,
                flags,
                mode,
                invalidArgumentIsOperationError);
        }

        return OpenAtNative(directoryFileDescriptor, name, flags, mode);
    }

    public static UnixMetadata ReadMetadata(int fileDescriptor)
    {
        var buffer = Marshal.AllocHGlobal(StatBufferSize);
        try
        {
            if (FStat(fileDescriptor, buffer) != 0)
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

    internal static int GetLinuxDirectoryFlag(Architecture architecture)
        => architecture switch
        {
            Architecture.X64 => 1 << 16,
            Architecture.Arm64 => 1 << 14,
            _ => throw UnsupportedArchitecture(),
        };

    internal static int GetLinuxNoFollowFlag(Architecture architecture)
        => architecture switch
        {
            Architecture.X64 => 1 << 17,
            Architecture.Arm64 => 1 << 15,
            _ => throw UnsupportedArchitecture(),
        };

    internal static UnixMetadata ReadLinuxMetadata(IntPtr buffer, Architecture architecture)
        => architecture switch
        {
            Architecture.X64 => new UnixMetadata(
                unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                unchecked((uint)Marshal.ReadInt32(buffer, 24)),
                Marshal.ReadInt64(buffer, 48),
                unchecked((ulong)Marshal.ReadInt64(buffer, 16)),
                unchecked((uint)Marshal.ReadInt32(buffer, 28)),
                unchecked((uint)Marshal.ReadInt32(buffer, 32))),
            Architecture.Arm64 => new UnixMetadata(
                unchecked((ulong)Marshal.ReadInt64(buffer, 0)),
                unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
                unchecked((uint)Marshal.ReadInt32(buffer, 16)),
                Marshal.ReadInt64(buffer, 48),
                unchecked((uint)Marshal.ReadInt32(buffer, 20)),
                unchecked((uint)Marshal.ReadInt32(buffer, 24)),
                unchecked((uint)Marshal.ReadInt32(buffer, 28))),
            _ => throw UnsupportedArchitecture(),
        };

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

        var directory = FdOpenDir(duplicate);
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
                var entry = ReadDir(directory);
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

    public static void Rename(int parentFileDescriptor, string oldName, string newName)
    {
        if (RenameAt(parentFileDescriptor, oldName, parentFileDescriptor, newName) != 0)
        {
            throw Error("renameat");
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

    public static void PublishHandleNoReplace(int sourceFileDescriptor, int parentFileDescriptor, string targetName)
    {
        if (OperatingSystem.IsLinux())
        {
            PublishLinuxAnonymousHandleNoReplace(sourceFileDescriptor, parentFileDescriptor, targetName);
            return;
        }

        var result = FCloneFileAt(sourceFileDescriptor, parentFileDescriptor, targetName, DarwinCloneNoFollow);
        if (result != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 17)
            {
                throw new LocalSecureFileAlreadyExistsException(targetName);
            }
            if (error is 22 or 38 or 45 or 95)
            {
                throw new PlatformNotSupportedException(
                    "The filesystem does not support exact-handle no-replace publication required by strict structured writes.",
                    new Win32Exception(error));
            }
            throw Error("exact-handle publication", error);
        }
    }

    internal static IDisposable OverrideAnonymousPublishErrors(
        Func<LocalUnixAnonymousPublishMethod, int?> errorOverride)
    {
        ArgumentNullException.ThrowIfNull(errorOverride);
        var previous = AnonymousPublishErrorOverride.Value;
        AnonymousPublishErrorOverride.Value = errorOverride;
        return new AnonymousPublishErrorOverrideScope(previous);
    }

    internal static IDisposable OverrideAnonymousTemporaryFileOpenError(int error)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(error);
        var previous = AnonymousTemporaryFileOpenErrorOverride.Value;
        AnonymousTemporaryFileOpenErrorOverride.Value = error;
        return new AnonymousTemporaryFileOpenErrorOverrideScope(previous);
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

    public static void Link(int parentFileDescriptor, string oldName, string newName)
    {
        if (LinkAt(parentFileDescriptor, oldName, parentFileDescriptor, newName, 0) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 17)
            {
                throw new LocalSecureFileAlreadyExistsException(newName);
            }
            throw Error("linkat", error);
        }
    }

    public static void Unlink(int parentFileDescriptor, string name, bool directory)
    {
        if (UnlinkAt(parentFileDescriptor, name, directory ? RemoveDirectoryFlag : 0) != 0)
        {
            throw Error("unlinkat");
        }
    }

    public static void Sync(int fileDescriptor)
    {
        if (FSync(fileDescriptor) != 0)
        {
            throw Error("fsync");
        }
    }

    public static void SetMode(int fileDescriptor, uint mode)
    {
        if (FChmod(fileDescriptor, mode) != 0)
        {
            throw Error("fchmod");
        }
    }

    public static void SetOwner(int fileDescriptor, uint userId, uint groupId)
    {
        if (FChown(fileDescriptor, userId, groupId) != 0)
        {
            throw Error("fchown");
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

    private static int OpenAt2(
        int directoryFileDescriptor,
        string name,
        int flags,
        uint mode,
        bool invalidArgumentIsOperationError)
    {
        EnsureSupportedArchitecture();
        var namePointer = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var how = new OpenHow
            {
                Flags = unchecked((ulong)flags),
                Mode = mode,
                Resolve = 0x01 | 0x02 | 0x04 | 0x08,
            };
            var result = SyscallOpenAt2(437, directoryFileDescriptor, namePointer, ref how, (nuint)24);
            if (result >= 0)
            {
                return checked((int)result);
            }

            var error = Marshal.GetLastPInvokeError();
            if (error == 38 || (error == 22 && !invalidArgumentIsOperationError))
            {
                throw new PlatformNotSupportedException(
                    "The Linux kernel does not provide the openat2 no-link/no-mount guarantees required by strict Local filesystem operations.",
                    new Win32Exception(error));
            }
            return -1;
        }
        finally
        {
            Marshal.FreeCoTaskMem(namePointer);
        }
    }

    private static void PublishLinuxAnonymousHandleNoReplace(
        int sourceFileDescriptor,
        int parentFileDescriptor,
        string targetName)
    {
        var (result, emptyPathError) = LinkAtForAnonymousPublish(
            LocalUnixAnonymousPublishMethod.EmptyPath,
            sourceFileDescriptor,
            string.Empty,
            parentFileDescriptor,
            targetName,
            AtEmptyPath);
        if (result == 0)
        {
            return;
        }
        if (emptyPathError == 17)
        {
            throw new LocalSecureFileAlreadyExistsException(targetName);
        }
        if (!CanRetryAnonymousPublish(emptyPathError))
        {
            throw Error("exact-handle publication", emptyPathError);
        }

        var descriptorPath = "/proc/self/fd/"
                             + sourceFileDescriptor.ToString(CultureInfo.InvariantCulture);
        var (procResult, procError) = LinkAtForAnonymousPublish(
            LocalUnixAnonymousPublishMethod.ProcSelfFd,
            AtCurrentWorkingDirectory,
            descriptorPath,
            parentFileDescriptor,
            targetName,
            AtSymlinkFollow);
        if (procResult == 0)
        {
            return;
        }
        if (procError == 17)
        {
            throw new LocalSecureFileAlreadyExistsException(targetName);
        }
        if (CanRetryAnonymousPublish(procError))
        {
            throw new LocalUnixAnonymousPublishUnavailableException(emptyPathError, procError);
        }
        throw Error("/proc/self/fd exact-handle publication", procError);
    }

    private static (int Result, int Error) LinkAtForAnonymousPublish(
        LocalUnixAnonymousPublishMethod method,
        int oldDirectoryFileDescriptor,
        string oldPath,
        int newDirectoryFileDescriptor,
        string newPath,
        int flags)
    {
        if (AnonymousPublishErrorOverride.Value?.Invoke(method) is { } forcedError)
        {
            return (-1, forcedError);
        }

        var result = LinkAt(
            oldDirectoryFileDescriptor,
            oldPath,
            newDirectoryFileDescriptor,
            newPath,
            flags);
        return (result, result == 0 ? 0 : Marshal.GetLastPInvokeError());
    }

    private static bool CanRetryAnonymousPublish(int error)
        => error is 1 or 2 or 13 or 18 or 20 or 22 or 38 or 40 or 95;

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

    private sealed class AnonymousPublishErrorOverrideScope(
        Func<LocalUnixAnonymousPublishMethod, int?>? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AnonymousPublishErrorOverride.Value = previous;
            }
        }
    }

    private sealed class AnonymousTemporaryFileOpenErrorOverrideScope(int? previous) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                AnonymousTemporaryFileOpenErrorOverride.Value = previous;
            }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

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

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallOpenAt2(
        long number,
        int directoryFileDescriptor,
        IntPtr path,
        ref OpenHow how,
        nuint size);

    [DllImport(LibC, EntryPoint = "fstat", SetLastError = true)]
    private static extern int FStat(int fileDescriptor, IntPtr buffer);

    [DllImport(LibC, EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MkdirAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        uint mode);

    [DllImport(LibC, EntryPoint = "renameat", SetLastError = true)]
    private static extern int RenameAt(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath);

    [DllImport(LibC, EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        uint flags);

    [DllImport(LibC, EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int RenameAtExclusive(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        uint flags = DarwinRenameExclusive);

    [DllImport(LibC, EntryPoint = "fclonefileat", SetLastError = true)]
    private static extern int FCloneFileAt(
        int sourceFileDescriptor,
        int destinationDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string destinationPath,
        uint flags);

    [DllImport(LibC, EntryPoint = "linkat", SetLastError = true)]
    private static extern int LinkAt(
        int oldDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath,
        int flags);

    [DllImport(LibC, EntryPoint = "unlinkat", SetLastError = true)]
    private static extern int UnlinkAt(
        int directoryFileDescriptor,
        [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags);

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

    [DllImport(LibC, EntryPoint = "fchmod", SetLastError = true)]
    private static extern int FChmod(int fileDescriptor, uint mode);

    [DllImport(LibC, EntryPoint = "fchown", SetLastError = true)]
    private static extern int FChown(int fileDescriptor, uint owner, uint group);

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

internal enum LocalUnixAnonymousPublishMethod
{
    EmptyPath,
    ProcSelfFd,
}

internal sealed class LocalUnixAnonymousPublishUnavailableException(
    int emptyPathError,
    int procError)
    : LocalSecurePathException(
        "Anonymous temporary-file publication is unavailable through both linkat(AT_EMPTY_PATH) "
        + $"({new Win32Exception(emptyPathError).Message}) and /proc/self/fd "
        + $"({new Win32Exception(procError).Message}).")
{
}
