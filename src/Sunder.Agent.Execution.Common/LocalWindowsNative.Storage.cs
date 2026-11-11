using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

internal static partial class LocalWindowsNative
{
    private const uint AttributeSparse = 0x00000200;
    private const uint AttributeCompressed = 0x00000800;
    private const uint GetCompression = 0x0009003c;
    private const uint SetCompression = 0x0009c040;
    private const uint SetSparse = 0x000900c4;

    private static void CopyStorageMetadata(SafeFileHandle source, SafeFileHandle destination, string sourcePath, string destinationPath)
    {
        var original = ReadStorageAttributes(source, sourcePath);
        var current = ReadStorageAttributes(destination, destinationPath);
        // Compression and sparseness belong to individual data streams. In
        // particular, creating a stream inside a compressed parent is not proof
        // that it has the same state as the corresponding original stream.
        var sourceFormat = ReadCompression(source, original, sourcePath);
        var destinationFormat = ReadCompression(destination, current, destinationPath);
        if (sourceFormat != destinationFormat)
            SetStorageControl(destination, SetCompression, BitConverter.GetBytes(sourceFormat), destinationPath);
        if ((original & AttributeSparse) != (current & AttributeSparse))
            SetStorageControl(destination, SetSparse, [(byte)((original & AttributeSparse) != 0 ? 1 : 0)], destinationPath);
        if (ReadCompression(destination, ReadStorageAttributes(destination, destinationPath), destinationPath) != sourceFormat
            || (ReadStorageAttributes(destination, destinationPath) & (AttributeSparse | AttributeCompressed))
               != (original & (AttributeSparse | AttributeCompressed)))
            throw new LocalSecurePathException($"The Windows stream storage metadata could not be preserved exactly: {destinationPath}");
    }

    private static uint ReadStorageAttributes(SafeFileHandle handle, string path)
    {
        if (!GetFileInformationByHandleExBasic(handle, FileBasicInfo, out var basic, (uint)Marshal.SizeOf<FileBasicInformation>()))
            throw Error("GetFileInformationByHandleEx(FileBasicInfo)", path);
        return basic.FileAttributes;
    }

    public static void ClearOwnedReadOnly(SafeFileHandle ownedFile, string path)
    {
        var attributes = ReadStorageAttributes(ownedFile, path);
        if ((attributes & 1 /* FILE_ATTRIBUTE_READONLY */) == 0) return;
        var basic = new FileBasicInformation { FileAttributes = attributes & ~(1U | AttributeSparse | AttributeCompressed) };
        if (basic.FileAttributes == 0) basic.FileAttributes = FileAttributeNormal;
        var buffer = Marshal.AllocHGlobal(Marshal.SizeOf<FileBasicInformation>());
        try
        {
            Marshal.StructureToPtr(basic, buffer, fDeleteOld: false);
            if (!SetFileInformationByHandle(ownedFile, FileBasicInfo, buffer, (uint)Marshal.SizeOf<FileBasicInformation>()))
                throw Error("SetFileInformationByHandle(owned cleanup attributes)", path);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static ushort ReadCompression(SafeFileHandle handle, uint attributes, string path)
    {
        var buffer = new byte[sizeof(ushort)];
        if (!DeviceIoControl(handle, GetCompression, null, 0, buffer, (uint)buffer.Length, out var count, IntPtr.Zero))
        {
            var error = Marshal.GetLastPInvokeError();
            // A filesystem which does not implement compression has no format to
            // transfer. This is not a fallback for a compressed stream, nor for a
            // failed write, flush or security operation.
            if (error is 1 or 50 && (attributes & AttributeCompressed) == 0) return 0;
            throw Error("DeviceIoControl(FSCTL_GET_COMPRESSION)", path, error);
        }
        if (count != sizeof(ushort))
            throw new LocalSecurePathException($"Windows returned incomplete stream compression metadata: {path}");
        return BitConverter.ToUInt16(buffer);
    }

    private static void SetStorageControl(SafeFileHandle handle, uint code, byte[] input, string path)
    {
        if (!DeviceIoControl(handle, code, input, (uint)input.Length, null, 0, out _, IntPtr.Zero))
            throw Error($"DeviceIoControl(0x{code:x8})", path);
    }

    [DllImport(Kernel32, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint code, byte[]? input, uint inputSize,
        [Out] byte[]? output, uint outputSize, out uint bytesReturned, IntPtr overlapped);
}
