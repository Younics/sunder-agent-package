using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Package.Agent.Shared.Importing;

internal static partial class BoundedImportIO
{
    private const int UnixReadOnly = 0;
    private const int UnixWriteOnly = 1;
    private static int UnixDirectoryFlag => OperatingSystem.IsMacOS() ? 0x00100000
        : RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 1 << 16,
            Architecture.Arm64 => 1 << 14,
            _ => throw UnsupportedArchitecture(),
        };
    private static int UnixNoFollowFlag => OperatingSystem.IsMacOS() ? 0x00000100
        : RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => 1 << 17,
            Architecture.Arm64 => 1 << 15,
            _ => throw UnsupportedArchitecture(),
        };
    private static int UnixCreateFlag => OperatingSystem.IsMacOS() ? 0x00000200 : 0x00000040;
    private static int UnixExclusiveFlag => OperatingSystem.IsMacOS() ? 0x00000800 : 0x00000080;
    private static int UnixCloseOnExecFlag => OperatingSystem.IsMacOS() ? 0x01000000 : 0x00080000;
    private static int UnixNonBlockingFlag => OperatingSystem.IsMacOS() ? 0x00000004 : 0x00000800;

    private static void EnsureSupportedArchitecture()
    {
        if (RuntimeInformation.ProcessArchitecture is not (Architecture.X64 or Architecture.Arm64)) throw UnsupportedArchitecture();
    }

    private static PlatformNotSupportedException UnsupportedArchitecture()
        => new($"Secure imports do not support architecture '{RuntimeInformation.ProcessArchitecture}'.");

    private static SafeFileHandle OpenUnixRoot(string path)
    {
        var descriptor = Open(path, UnixReadOnly | UnixDirectoryFlag | UnixNoFollowFlag | UnixCloseOnExecFlag);
        if (descriptor < 0) ThrowOpenError(path, Marshal.GetLastPInvokeError());
        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    private static SafeFileHandle OpenUnixRelative(SafeFileHandle parent, string name, bool directory, string path)
    {
        var flags = UnixReadOnly | UnixNoFollowFlag | UnixCloseOnExecFlag | UnixNonBlockingFlag
            | (directory ? UnixDirectoryFlag : 0);
        var descriptor = OpenAt(parent.DangerousGetHandle().ToInt32(), name, flags, 0);
        if (descriptor < 0) ThrowOpenError(path, Marshal.GetLastPInvokeError());
        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    private static void CreateUnixDirectory(SafeFileHandle parent, string name, string path)
    {
        if (MakeDirectoryAt(parent.DangerousGetHandle().ToInt32(), name, Convert.ToUInt32("700", 8)) != 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error != 17)
                throw new IOException($"Could not create import staging directory '{path}'.", new Win32Exception(error));
        }
    }

    private static SafeFileHandle CreateUnixRegularFile(SafeFileHandle parent, string name, string path)
    {
        var descriptor = OpenAt(parent.DangerousGetHandle().ToInt32(), name,
            UnixWriteOnly | UnixCreateFlag | UnixExclusiveFlag | UnixNoFollowFlag | UnixCloseOnExecFlag,
            Convert.ToUInt32("600", 8));
        if (descriptor < 0)
        {
            var error = Marshal.GetLastPInvokeError();
            if (error == 17) throw new IOException($"Import staging target already exists: '{path}'.");
            throw new IOException($"Could not create import staging file '{path}'.", new Win32Exception(error));
        }
        return new SafeFileHandle(new IntPtr(descriptor), ownsHandle: true);
    }

    private static IEnumerable<string> EnumerateUnixDirectoryNames(SafeFileHandle directory, string directoryPath)
    {
        var duplicate = OpenAt(directory.DangerousGetHandle().ToInt32(), ".",
            UnixReadOnly | UnixDirectoryFlag | UnixNoFollowFlag | UnixCloseOnExecFlag, 0);
        if (duplicate < 0)
            throw new IOException($"Could not enumerate import directory '{directoryPath}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
        var nativeDirectory = FileDescriptorOpenDirectory(duplicate);
        if (nativeDirectory == IntPtr.Zero)
        {
            var error = Marshal.GetLastPInvokeError();
            _ = Close(duplicate);
            throw new IOException($"Could not enumerate import directory '{directoryPath}'.", new Win32Exception(error));
        }
        try
        {
            while (true)
            {
                Marshal.SetLastPInvokeError(0);
                var entry = ReadDirectory(nativeDirectory);
                if (entry == IntPtr.Zero)
                {
                    var error = Marshal.GetLastPInvokeError();
                    if (error != 0)
                        throw new IOException($"Could not enumerate import directory '{directoryPath}'.", new Win32Exception(error));
                    yield break;
                }
                var nameOffset = OperatingSystem.IsMacOS() ? 21 : 19;
                var recordLength = unchecked((ushort)Marshal.ReadInt16(entry, 16));
                if (recordLength <= nameOffset)
                    throw new InvalidDataException($"Import directory '{directoryPath}' returned an invalid native entry.");
                var maximumNameBytes = recordLength - nameOffset;
                if (OperatingSystem.IsMacOS())
                {
                    var declaredNameBytes = unchecked((ushort)Marshal.ReadInt16(entry, 18));
                    if (declaredNameBytes >= maximumNameBytes)
                        throw new InvalidDataException($"Import directory '{directoryPath}' returned an invalid native name.");
                    maximumNameBytes = declaredNameBytes + 1;
                }
                var name = ReadNullTerminatedUtf8(entry + nameOffset, maximumNameBytes, directoryPath);
                if (name is not "." and not "..") yield return name;
            }
        }
        finally { _ = CloseDirectory(nativeDirectory); }
    }

    private static string ReadNullTerminatedUtf8(IntPtr pointer, int maximumBytes, string directoryPath)
    {
        var length = 0;
        while (length < maximumBytes && Marshal.ReadByte(pointer, length) != 0) length++;
        if (length == maximumBytes)
            throw new InvalidDataException($"Import directory '{directoryPath}' returned an unterminated native name.");
        var bytes = new byte[length];
        Marshal.Copy(pointer, bytes, 0, length);
        try { return StrictUtf8.GetString(bytes); }
        catch (DecoderFallbackException exception)
        {
            throw new InvalidDataException($"Import directory '{directoryPath}' contains a name that is not valid UTF-8.", exception);
        }
    }

    private static ImportNodeIdentity ReadUnixIdentity(SafeFileHandle handle, string path)
    {
        var buffer = Marshal.AllocHGlobal(256);
        try
        {
            if (FStat(handle.DangerousGetHandle().ToInt32(), buffer) != 0)
                throw new IOException($"Could not inspect import source '{path}'.", new Win32Exception(Marshal.GetLastPInvokeError()));
            return OperatingSystem.IsMacOS() ? ReadMacIdentity(buffer, path) : ReadLinuxIdentity(buffer, path);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ImportNodeIdentity ReadMacIdentity(IntPtr buffer, string path)
    {
        EnsureSupportedArchitecture();
        var mode = unchecked((ushort)Marshal.ReadInt16(buffer, 4));
        return new ImportNodeIdentity(unchecked((ulong)(uint)Marshal.ReadInt32(buffer, 0)),
            unchecked((ulong)Marshal.ReadInt64(buffer, 8)), 0, ReadKind(mode, path), Marshal.ReadInt64(buffer, 96),
            unchecked((ushort)Marshal.ReadInt16(buffer, 6)), Marshal.ReadInt64(buffer, 48), Marshal.ReadInt64(buffer, 56),
            Marshal.ReadInt64(buffer, 64), Marshal.ReadInt64(buffer, 72));
    }

    private static ImportNodeIdentity ReadLinuxIdentity(IntPtr buffer, string path)
    {
        EnsureSupportedArchitecture();
        var architecture = RuntimeInformation.ProcessArchitecture;
        var mode = unchecked((uint)Marshal.ReadInt32(buffer, architecture == Architecture.X64 ? 24 : 16));
        var linkCount = architecture == Architecture.X64 ? unchecked((ulong)Marshal.ReadInt64(buffer, 16))
            : unchecked((uint)Marshal.ReadInt32(buffer, 20));
        return new ImportNodeIdentity(unchecked((ulong)Marshal.ReadInt64(buffer, 0)), unchecked((ulong)Marshal.ReadInt64(buffer, 8)),
            0, ReadKind(mode, path), Marshal.ReadInt64(buffer, 48), linkCount, Marshal.ReadInt64(buffer, 88),
            Marshal.ReadInt64(buffer, 96), Marshal.ReadInt64(buffer, 104), Marshal.ReadInt64(buffer, 112));
    }

    private static ImportNodeKind ReadKind(uint mode, string path)
        => (mode & 0xf000) switch
        {
            0x4000 => ImportNodeKind.Directory,
            0x8000 => ImportNodeKind.RegularFile,
            _ => throw new InvalidDataException($"Import source contains a device, socket, FIFO, or unsupported entry: '{path}'."),
        };

    [DllImport("libc", EntryPoint = "open", SetLastError = true)]
    private static extern int Open(string path, int flags);

    private static int OpenAt(int directoryFileDescriptor, string path, int flags, uint mode)
        => OperatingSystem.IsMacOS() && RuntimeInformation.ProcessArchitecture == Architecture.Arm64
            ? OpenAtAppleArm64(directoryFileDescriptor, path, flags, 0, 0, 0, 0, 0, mode)
            : OpenAtNative(directoryFileDescriptor, path, flags, mode);

    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtNative(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    // Apple ARM64 puts variadic arguments on the stack. Fill x3-x7 so mode
    // occupies the first stack slot, not a fixed-argument register.
    // https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms
    [DllImport("libc", EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtAppleArm64(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, uint mode);

    [DllImport("libc", EntryPoint = "fstat", SetLastError = true)] private static extern int FStat(int descriptor, IntPtr buffer);
    [DllImport("libc", EntryPoint = "mkdirat", SetLastError = true)]
    private static extern int MakeDirectoryAt(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path, uint mode);
    [DllImport("libc", EntryPoint = "fdopendir", SetLastError = true)] private static extern IntPtr FileDescriptorOpenDirectory(int fileDescriptor);
    [DllImport("libc", EntryPoint = "readdir", SetLastError = true)] private static extern IntPtr ReadDirectory(IntPtr directory);
    [DllImport("libc", EntryPoint = "closedir", SetLastError = true)] private static extern int CloseDirectory(IntPtr directory);
    [DllImport("libc", EntryPoint = "close", SetLastError = true)] private static extern int Close(int descriptor);
}
