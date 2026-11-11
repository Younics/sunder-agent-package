using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.Win32.SafeHandles;

namespace Sunder.Agent.Execution.Common;

internal static partial class LocalWindowsNative
{
    private const int StreamNameBudget = 64 * 1024;
    private const int StreamCountBudget = 256;
    private const long StreamCopyBudget = 64L * 1024 * 1024;

    public static AlternateStreamSnapshot CopyAlternateStreams(SafeFileHandle source, SafeFileHandle destination,
        string sourcePath, string destinationPath)
    {
        var snapshot = new AlternateStreamSnapshot(source, sourcePath);
        try
        {
            var sourceIdentity = ReadMetadata(source, sourcePath);
            var destinationIdentity = ReadMetadata(destination, destinationPath);
            var names = ReadAlternateStreams(source, sourcePath);
            long totalBytes = 0;
            foreach (var (name, length) in names)
            {
                if (length > StreamCopyBudget - totalBytes)
                    throw new LocalSecurePathException($"Windows alternate streams exceed the {StreamCopyBudget}-byte metadata budget: {sourcePath}");
                totalBytes += length;
                // Sharing is per data stream, while delete sharing is checked
                // across all streams. Lock each original stream against writers;
                // share DELETE with the default-stream quarantine handle.
                var read = OpenRelative(source, name, GenericRead, false, false, true, sourcePath, ShareRead | ShareDelete);
                SafeFileHandle? write = null;
                try
                {
                    RequireStreamIdentity(read, sourceIdentity, length, sourcePath);
                    write = OpenRelative(destination, name, GenericRead | GenericWrite, false, true, true,
                        destinationPath, ShareRead | ShareDelete);
                    RequireStreamIdentity(write, destinationIdentity, 0, destinationPath);
                    using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var buffer = new byte[64 * 1024];
                    long offset = 0;
                    while (offset < length)
                    {
                        var count = RandomAccess.Read(read, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                        if (count == 0) throw new LocalSecureContentChangedException(sourcePath);
                        RandomAccess.Write(write, buffer.AsSpan(0, count), offset);
                        digest.AppendData(buffer, 0, count);
                        offset += count;
                    }
                    Flush(write, destinationPath);
                    snapshot.Add(new(name, length, read, write, digest.GetHashAndReset()));
                    write = null;
                }
                catch { read.Dispose(); throw; }
                finally { write?.Dispose(); }
            }
            snapshot.ValidateSource();
            return snapshot;
        }
        catch { snapshot.Dispose(); throw; }
    }

    public static void SanitizeAlternateStreams(SafeFileHandle ownedFile, string path)
    {
        var identity = ReadMetadata(ownedFile, path);
        foreach (var (name, _) in ReadAlternateStreams(ownedFile, path))
        {
            // The file's creation identity, not a pathname or an ADS name alone,
            // authorizes cleanup. This never follows a replacement file or alias.
            using var stream = OpenRelative(ownedFile, name, GenericRead | GenericWrite, false, false, true, path, ShareAll);
            var metadata = ReadMetadata(stream, path);
            RequireStreamIdentity(stream, identity, metadata.Length, path);
            RandomAccess.SetLength(stream, 0);
            Flush(stream, path);
            RequireStreamIdentity(stream, identity, 0, path);
        }
    }

    private static void RequireStreamIdentity(SafeFileHandle handle, WindowsMetadata expected, long length, string path)
    {
        var current = ReadMetadata(handle, path);
        if (current.Kind != LocalSecureNodeKind.RegularFile || current.Volume != expected.Volume
            || current.FileId != expected.FileId || current.FileIdHigh != expected.FileIdHigh
            || current.Length != length || current.LinkCount != expected.LinkCount)
            throw new LocalSecureApprovalChangedException(path);
    }

    private static List<(string Name, long Length)> ReadAlternateStreams(SafeFileHandle source, string path)
    {
        var buffer = Marshal.AllocHGlobal(StreamNameBudget);
        try
        {
            if (!GetFileInformationByHandleExBuffer(source, 7 /* FileStreamInfo */, buffer, StreamNameBudget))
                throw Error("GetFileInformationByHandleEx(FileStreamInfo)", path);
            var streams = new List<(string Name, long Length)>();
            var offset = 0;
            var visited = 0;
            while (true)
            {
                if (++visited > StreamCountBudget || offset > StreamNameBudget - 24)
                    throw new LocalSecurePathException($"Windows alternate-stream metadata exceeds its bounded inventory: {path}");
                var entry = buffer + offset;
                var next = Marshal.ReadInt32(entry);
                var nameLength = Marshal.ReadInt32(entry, 4);
                var length = Marshal.ReadInt64(entry, 8);
                if (nameLength < 0 || nameLength % sizeof(char) != 0 || nameLength > StreamNameBudget - offset - 24 || length < 0)
                    throw InvalidStreamMetadata(path);
                var name = Marshal.PtrToStringUni(entry + 24, nameLength / sizeof(char)) ?? throw InvalidStreamMetadata(path);
                // Only exact data-stream child names can reach NtCreateFile.
                // A security/backup record can never smuggle a path, link or type.
                if (name.Length < 7 || name[0] != ':' || !name.EndsWith(":$DATA", StringComparison.Ordinal)
                    || name.AsSpan(1, name.Length - 7).IndexOfAny('/', '\\', '\0') >= 0
                    || name.AsSpan(1, name.Length - 7).Contains(':'))
                    throw InvalidStreamMetadata(path);
                if (name != "::$DATA") streams.Add((name, length));
                if (next == 0) break;
                if (next < 24 + nameLength || next % 8 != 0 || next > StreamNameBudget - offset - 24)
                    throw InvalidStreamMetadata(path);
                offset += next;
            }
            streams.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
            if (streams.Select(static stream => stream.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != streams.Count)
                throw InvalidStreamMetadata(path);
            return streams;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    private static LocalSecurePathException InvalidStreamMetadata(string path)
        => new($"Windows returned invalid alternate-stream metadata: {path}");

    internal sealed record AlternateStream(string Name, long Length, SafeFileHandle Source, SafeFileHandle Destination, byte[] Digest);

    internal sealed class AlternateStreamSnapshot(SafeFileHandle source, string path) : IDisposable
    {
        private readonly List<AlternateStream> _streams = [];
        internal void Add(AlternateStream stream) => _streams.Add(stream);

        internal void ValidateSource()
        {
            if (!ReadAlternateStreams(source, path).SequenceEqual(_streams.Select(static stream => (stream.Name, stream.Length))))
                throw new LocalSecureContentChangedException(path);
            foreach (var stream in _streams)
            {
                if (RandomAccess.GetLength(stream.Source) != stream.Length || RandomAccess.GetLength(stream.Destination) != stream.Length
                    || !Hash(stream.Source, stream.Length).AsSpan().SequenceEqual(stream.Digest)
                    || !Hash(stream.Destination, stream.Length).AsSpan().SequenceEqual(stream.Digest))
                    throw new LocalSecureContentChangedException(path);
            }
        }

        private static byte[] Hash(SafeFileHandle handle, long length)
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[64 * 1024];
            long offset = 0;
            while (offset < length)
            {
                var count = RandomAccess.Read(handle, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - offset)), offset);
                if (count == 0) throw new LocalSecurePathException("A Windows alternate stream changed during metadata preservation.");
                hash.AppendData(buffer, 0, count);
                offset += count;
            }
            return hash.GetHashAndReset();
        }

        public void Dispose()
        {
            foreach (var stream in _streams) { stream.Destination.Dispose(); stream.Source.Dispose(); }
            _streams.Clear();
        }
    }
}
