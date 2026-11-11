using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

internal static partial class LocalWindowsNative
{
    private const int ExtendedAttributeBudget = 64 * 1024;

    private static byte[] CopyExtendedAttributes(SafeFileHandle source, SafeFileHandle destination,
        string sourcePath, string destinationPath)
    {
        var attributes = ReadExtendedAttributes(source, sourcePath);
        if (attributes.Length == 0) return attributes;
        var status = NtSetEaFile(destination, out _, attributes, checked((uint)attributes.Length));
        if (status < 0) throw Error("NtSetEaFile", destinationPath, checked((int)RtlNtStatusToDosError(status)));
        if (!ReadExtendedAttributes(destination, destinationPath).AsSpan().SequenceEqual(attributes))
            throw new LocalSecurePathException($"The Windows extended attributes could not be preserved exactly: {destinationPath}");
        return attributes;
    }

    internal sealed class FileMetadataSnapshot(SafeFileHandle source, SafeFileHandle destination, string sourcePath, string destinationPath,
        long creationTime, long lastWriteTime, long changeTime, uint attributes, byte[] extendedAttributes)
    {
        internal void Validate()
        {
            if (!GetFileInformationByHandleExBasic(source, FileBasicInfo, out var current, (uint)Marshal.SizeOf<FileBasicInformation>()))
                throw Error("GetFileInformationByHandleEx(FileBasicInfo)", sourcePath);
            // Reads may legitimately update last-access time. Creation, write,
            // change time and attributes must still identify the copied snapshot.
            if (current.CreationTime != creationTime || current.LastWriteTime != lastWriteTime
                || current.ChangeTime != changeTime || current.FileAttributes != attributes
                || !ReadExtendedAttributes(source, sourcePath).AsSpan().SequenceEqual(extendedAttributes)
                || !ReadExtendedAttributes(destination, destinationPath).AsSpan().SequenceEqual(extendedAttributes))
                throw new LocalSecureContentChangedException(sourcePath);
        }
    }

    private static byte[] ReadExtendedAttributes(SafeFileHandle source, string path)
    {
        var status = NtQueryInformationFile(source, out var completion, out var eaSize, sizeof(uint), 7 /* FileEaInformation */);
        if (status < 0) throw Error("NtQueryInformationFile(FileEaInformation)", path, checked((int)RtlNtStatusToDosError(status)));
        if (completion.Information.ToInt64() != sizeof(uint))
            throw new LocalSecurePathException($"The Windows extended-attribute query returned incomplete metadata: {path}");
        if (eaSize == 0) return [];
        if (eaSize > ExtendedAttributeBudget)
            throw new LocalSecurePathException($"Windows extended attributes exceed the {ExtendedAttributeBudget}-byte metadata budget: {path}");
        var buffer = new byte[ExtendedAttributeBudget];
        status = NtQueryEaFile(source, out completion, buffer, (uint)buffer.Length, false, IntPtr.Zero, 0, IntPtr.Zero, true);
        if (status < 0) throw Error("NtQueryEaFile", path, checked((int)RtlNtStatusToDosError(status)));
        var length = completion.Information.ToInt64();
        if (length < 9 || length > buffer.Length)
            throw new LocalSecurePathException($"The Windows extended-attribute query returned an invalid length: {path}");
        // Normalize unused alignment bytes before copying or comparing. Neither
        // kernel padding nor ordering is part of an attribute's value.
        var entries = new List<byte[]>();
        var offset = 0;
        while (true)
        {
            if (offset > length - 9) throw InvalidExtendedAttributes(path);
            var next = BitConverter.ToUInt32(buffer, offset);
            var nameLength = buffer[offset + 5];
            var valueLength = BitConverter.ToUInt16(buffer, offset + 6);
            var entryLength = 9 + nameLength + valueLength;
            if (nameLength == 0 || entryLength > length - offset || buffer[offset + 8 + nameLength] != 0
                || buffer.AsSpan(offset + 8, nameLength).Contains((byte)0))
                throw InvalidExtendedAttributes(path);
            var entry = buffer.AsSpan(offset, entryLength).ToArray();
            entry.AsSpan(0, 4).Clear();
            entries.Add(entry);
            if (next == 0) break;
            if (next < entryLength || next % 4 != 0 || next > length - offset - 9)
                throw InvalidExtendedAttributes(path);
            offset += checked((int)next);
        }
        entries.Sort(static (left, right) => left.AsSpan(8, left[5]).SequenceCompareTo(right.AsSpan(8, right[5])));
        var normalized = new byte[entries.Sum(static entry => (entry.Length + 3) & ~3)];
        offset = 0;
        for (var index = 0; index < entries.Count; index++)
        {
            var entry = entries[index];
            var alignedLength = (entry.Length + 3) & ~3;
            entry.CopyTo(normalized, offset);
            if (index < entries.Count - 1)
                BitConverter.TryWriteBytes(normalized.AsSpan(offset, 4), checked((uint)alignedLength));
            offset += alignedLength;
        }
        return normalized;
    }

    private static LocalSecurePathException InvalidExtendedAttributes(string path)
        => new($"Windows returned an invalid extended-attribute list: {path}");

    [DllImport(Ntdll)]
    private static extern int NtSetEaFile(SafeFileHandle file, out IoStatusBlock completion, byte[] buffer, uint length);

    [DllImport(Ntdll)]
    private static extern int NtQueryEaFile(SafeFileHandle file, out IoStatusBlock completion, [Out] byte[] buffer, uint length,
        [MarshalAs(UnmanagedType.U1)] bool returnSingleEntry, IntPtr eaList, uint eaListLength, IntPtr eaIndex,
        [MarshalAs(UnmanagedType.U1)] bool restartScan);
}
