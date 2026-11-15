using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Sunder.Agent.Execution.Common;

internal static partial class LocalUnixNative
{
    private const uint LinuxRenameNoReplace = 0x00000001;

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

    private static int OpenAt2(int directoryFileDescriptor, string name, int flags, uint mode, bool invalidArgumentIsOperationError)
    {
        EnsureSupportedArchitecture();
        var namePointer = Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            var how = new OpenHow { Flags = unchecked((ulong)flags), Mode = mode, Resolve = 0x01 | 0x02 | 0x04 | 0x08 };
            var result = SyscallOpenAt2(437, directoryFileDescriptor, namePointer, ref how, (nuint)24);
            if (result >= 0) return checked((int)result);
            var error = Marshal.GetLastPInvokeError();
            if (error == 38 || (error == 22 && !invalidArgumentIsOperationError))
                throw new PlatformNotSupportedException(
                    "The Linux kernel does not provide the openat2 no-link/no-mount guarantees required by strict Local filesystem operations.",
                    new Win32Exception(error));
            return -1;
        }
        finally { Marshal.FreeCoTaskMem(namePointer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenHow
    {
        public ulong Flags;
        public ulong Mode;
        public ulong Resolve;
    }

    [DllImport(LibC, EntryPoint = "syscall", SetLastError = true)]
    private static extern long SyscallOpenAt2(long number, int directoryFileDescriptor, IntPtr path, ref OpenHow how, nuint size);

    [DllImport(LibC, EntryPoint = "renameat2", SetLastError = true)]
    private static extern int RenameAt2(int oldDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string oldPath,
        int newDirectoryFileDescriptor, [MarshalAs(UnmanagedType.LPUTF8Str)] string newPath, uint flags);
}
