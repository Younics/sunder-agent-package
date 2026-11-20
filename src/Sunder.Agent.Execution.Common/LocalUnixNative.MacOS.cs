using System.Runtime.InteropServices;

namespace Sunder.Agent.Execution.Common;

internal static partial class LocalUnixNative
{
    private const uint DarwinRenameExclusive = 0x00000004;

    // The parsed stat/dirent layouts use 64-bit inodes. Darwin x64 retains the
    // legacy layouts under bare symbols; ARM64 exports only the bare inode64 ABI.
    [DllImport(LibC, EntryPoint = "fstat$INODE64", SetLastError = true)]
    private static extern int FStatDarwinInode64(int fileDescriptor, IntPtr buffer);

    [DllImport(LibC, EntryPoint = "fdopendir$INODE64", SetLastError = true)]
    private static extern IntPtr FdOpenDirDarwinInode64(int fileDescriptor);

    [DllImport(LibC, EntryPoint = "readdir$INODE64", SetLastError = true)]
    private static extern IntPtr ReadDirDarwinInode64(IntPtr directory);

    // Apple ARM64 variadic arguments start on the stack. These unused register
    // arguments place mode in its first stack slot, rather than in x2/x3.
    // Keep the ordinary signatures in LocalUnixNative.cs for Linux and macOS x64.
    // https://developer.apple.com/documentation/xcode/writing-arm64-code-for-apple-platforms
    [DllImport(LibC, EntryPoint = "open", SetLastError = true)]
    private static extern int OpenAppleArm64([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags,
        nint unused2, nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, uint mode);

    [DllImport(LibC, EntryPoint = "openat", SetLastError = true)]
    private static extern int OpenAtAppleArm64(int directoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string path,
        int flags, nint unused3, nint unused4, nint unused5, nint unused6, nint unused7, uint mode);

    [DllImport(LibC, EntryPoint = "renameatx_np", SetLastError = true)]
    private static extern int RenameAtExclusive(int oldDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags = DarwinRenameExclusive);
}
