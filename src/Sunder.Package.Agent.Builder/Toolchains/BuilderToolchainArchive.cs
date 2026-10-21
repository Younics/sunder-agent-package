using System.Formats.Tar;
using System.IO.Compression;
using System.Text;

namespace Sunder.Package.Agent.Builder;

/// <summary>Extracts verified vendor archives into a new private directory, never through archive-provided links.</summary>
public static class BuilderToolchainArchive
{
    public static async Task ExtractAsync(string archivePath, string destination, string? archiveRoot,
        CancellationToken cancellationToken = default)
    {
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Extraction requires a new staging directory.");
        Directory.CreateDirectory(destination);
        BuilderToolchainFiles.RejectLink(destination);
        var extraction = new Extraction(destination, archiveRoot);
        await using var input = File.OpenRead(archivePath);
        if (archivePath.EndsWith(".zip", StringComparison.Ordinal))
            await extraction.ZipAsync(input, cancellationToken).ConfigureAwait(false);
        else if (archivePath.EndsWith(".tar.gz", StringComparison.Ordinal))
            await extraction.TarAsync(input, cancellationToken).ConfigureAwait(false);
        else throw new InvalidDataException("Only official ZIP and tar.gz toolchain archives are supported.");
        extraction.CreateLinks(cancellationToken);
    }

    private sealed class Extraction(string root, string? archiveRoot)
    {
        private sealed record Entry(string Path, bool Directory, bool Link = false);
        private sealed record Link(string Path, string Target, bool Hard);
        private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> explicitEntries = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Link> links = [];
        private long expanded;
        private int count;

        public async Task ZipAsync(Stream source, CancellationToken token)
        {
            using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
            foreach (var entry in zip.Entries)
            {
                token.ThrowIfCancellationRequested();
                var type = (entry.ExternalAttributes >> 16) & 0xF000;
                var directory = entry.FullName.EndsWith('/');
                if (type is not (0 or 0x8000 or 0x4000 or 0xA000) ||
                    (type == 0x4000 && !directory) || (directory && type is 0x8000 or 0xA000) ||
                    (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("The ZIP contains an unsupported filesystem entry.");
                var path = Register(entry.FullName, directory, type == 0xA000);
                if (path is null) continue;
                if (directory) { Directory.CreateDirectory(Full(path)); continue; }
                await using var data = entry.Open();
                if (type == 0xA000)
                {
                    if (entry.Length > 4096) throw new InvalidDataException("The ZIP symlink target is too long.");
                    using var reader = new StreamReader(data, new UTF8Encoding(false, true));
                    var buffer = new char[4097];
                    var read = await reader.ReadBlockAsync(buffer, token).ConfigureAwait(false);
                    if (read > 4096) throw new InvalidDataException("The ZIP symlink target is too long.");
                    var target = new string(buffer, 0, read);
                    links.Add(new(path, LinkTarget(path, target, hard: false), false));
                }
                else await WriteAsync(path, data, entry.Length, (UnixFileMode)(entry.ExternalAttributes >> 16), token).ConfigureAwait(false);
            }
        }

        public async Task TarAsync(Stream source, CancellationToken token)
        {
            await using var gzip = new GZipStream(source, CompressionMode.Decompress, leaveOpen: true);
            using var reader = new TarReader(gzip, leaveOpen: true);
            while (await reader.GetNextEntryAsync(copyData: false, cancellationToken: token).ConfigureAwait(false) is { } entry)
            {
                token.ThrowIfCancellationRequested();
                // Extended metadata is interpreted by TarReader, not materialized as filesystem objects.
                if (entry.EntryType == TarEntryType.GlobalExtendedAttributes) continue;
                var directory = entry.EntryType == TarEntryType.Directory;
                var link = entry.EntryType is TarEntryType.SymbolicLink or TarEntryType.HardLink;
                if (!directory && !link && entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new InvalidDataException("The TAR contains a device, sparse file, or unsupported entry.");
                var path = Register(entry.Name, directory, link);
                if (path is null) continue;
                if (directory) Directory.CreateDirectory(Full(path));
                else if (link)
                {
                    var hard = entry.EntryType == TarEntryType.HardLink;
                    links.Add(new(path, LinkTarget(path, entry.LinkName, hard), hard));
                }
                else await WriteAsync(path, entry.DataStream ?? Stream.Null, entry.Length, entry.Mode, token).ConfigureAwait(false);
            }
        }

        private string? Register(string name, bool directory, bool link)
        {
            if (++count > BuilderToolchainPolicy.MaximumArchiveEntries) throw new InvalidDataException("The toolchain archive contains too many entries.");
            var path = NormalizeName(name);
            if (archiveRoot is not null)
            {
                if (path == archiveRoot && directory) return null;
                if (!path.StartsWith(archiveRoot + '/', StringComparison.Ordinal)) throw new InvalidDataException("An archive entry is outside the vendor distribution root.");
                path = path[(archiveRoot.Length + 1)..];
            }
            if (path.Length == 0 && directory) return null;
            if (path.Length == 0 || string.Equals(path.Split('/')[0], BuilderToolchainFiles.ReceiptName, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("An archive entry uses a reserved path.");
            var segments = path.Split('/');
            for (var i = 1; i < segments.Length; i++) Add(string.Join('/', segments[..i]), directory: true, link: false, explicitEntry: false);
            Add(path, directory, link, explicitEntry: true);
            Directory.CreateDirectory(Path.GetDirectoryName(Full(path))!);
            return path;
        }

        private void Add(string path, bool directory, bool link, bool explicitEntry)
        {
            if (entries.TryGetValue(path, out var prior))
            {
                if (prior.Path != path || !prior.Directory || !directory || link || prior.Link)
                    throw new InvalidDataException("The toolchain archive contains a path/case collision or writes through a link.");
            }
            else entries.Add(path, new(path, directory, link));
            if (explicitEntry && !explicitEntries.Add(path)) throw new InvalidDataException("The toolchain archive contains a duplicate entry.");
        }

        private async Task WriteAsync(string path, Stream source, long length, UnixFileMode mode, CancellationToken token)
        {
            AddLength(length);
            await using (var output = new FileStream(Full(path), FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
            {
                var buffer = new byte[64 * 1024];
                long written = 0;
                while (true)
                {
                    var read = await source.ReadAsync(buffer, token).ConfigureAwait(false);
                    if (read == 0) break;
                    written += read;
                    if (written > length) throw new InvalidDataException("An archive entry exceeds its declared size.");
                    await output.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                }
                if (written != length) throw new EndOfStreamException("An archive entry is truncated.");
            }
            if (!OperatingSystem.IsWindows())
            {
                // Owner-writable, no special permission bits, preserve executability for SDK/apphosts/npm.
                var safeMode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;
                if ((mode & (UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute)) != 0)
                    safeMode |= UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute;
                File.SetUnixFileMode(Full(path), safeMode);
            }
        }

        private string LinkTarget(string path, string target, bool hard)
        {
            if (target.Length > 4096 || target.Length == 0 || target.StartsWith('/') || target.Contains('\\') || target.Contains(':') || target.Any(char.IsControl))
                throw new InvalidDataException("An archive link has an unsafe target.");
            if (hard)
            {
                var normalized = NormalizeName(target);
                if (archiveRoot is null) return normalized;
                if (!normalized.StartsWith(archiveRoot + '/', StringComparison.Ordinal)) throw new InvalidDataException("A hardlink leaves the distribution root.");
                return normalized[(archiveRoot.Length + 1)..];
            }
            var combined = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Full(path))!, target));
            if (!BuilderToolchainFiles.IsChild(combined, root)) throw new InvalidDataException("An archive symlink leaves the distribution root.");
            return NormalizeName(Path.GetRelativePath(root, combined).Replace(Path.DirectorySeparatorChar, '/'));
        }

        public void CreateLinks(CancellationToken token)
        {
            // All regular entries exist before links are created. Links never become extraction parents.
            // Resolve the complete graph first so a chain/cycle/dangling target cannot escape later.
            var linkMap = links.ToDictionary(static link => link.Path, StringComparer.Ordinal);
            Entry Resolve(string path, HashSet<string> visiting)
            {
                if (!entries.TryGetValue(path, out var entry) || entry.Path != path) throw new InvalidDataException("An archive link has a missing or case-mismatched target.");
                if (!entry.Link) return entry;
                if (!visiting.Add(path)) throw new InvalidDataException("The archive contains a link cycle.");
                return Resolve(linkMap[path].Target, visiting);
            }
            var resolved = links.Select(link => (Link: link, Target: Resolve(link.Path, new(StringComparer.Ordinal)))).ToArray();
            foreach (var (link, target) in resolved)
            {
                token.ThrowIfCancellationRequested();
                if (link.Hard)
                {
                    if (target.Directory) throw new InvalidDataException("Directory hardlinks are not supported.");
                    AddLength(new FileInfo(Full(target.Path)).Length);
                    File.Copy(Full(target.Path), Full(link.Path), overwrite: false);
                }
                else
                {
                    // Normalize to the final internal target, retaining genuine npm command symlinks.
                    var relative = Path.GetRelativePath(Path.GetDirectoryName(Full(link.Path))!, Full(target.Path));
                    if (target.Directory) Directory.CreateSymbolicLink(Full(link.Path), relative);
                    else File.CreateSymbolicLink(Full(link.Path), relative);
                }
            }
        }

        private string Full(string path) => Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));

        private void AddLength(long length)
        {
            if (length < 0 || length > BuilderToolchainPolicy.MaximumArchiveBytes ||
                (expanded = checked(expanded + length)) > BuilderToolchainPolicy.MaximumExpandedBytes)
                throw new InvalidDataException("The expanded toolchain archive exceeds its size limit.");
        }

        private static string NormalizeName(string name)
        {
            if (name.Length > 4096 || name.StartsWith('/') || name.Contains('\\') || name.Contains(':') || name.Any(char.IsControl))
                throw new InvalidDataException("The toolchain archive contains an unsafe path.");
            while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimEnd('/');
            if (name == "." || name.Length == 0) return "";
            foreach (var segment in name.Split('/'))
            {
                var stem = segment.Split('.')[0].ToUpperInvariant();
                if (segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
                    segment.IndexOfAny(['<', '>', '"', '|', '?', '*']) >= 0 ||
                    stem is "CON" or "PRN" or "AUX" or "NUL" ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '0' and <= '9'))
                    throw new InvalidDataException("The toolchain archive contains an unsafe path segment.");
            }
            if (!name.IsNormalized(NormalizationForm.FormC)) throw new InvalidDataException("Noncanonical Unicode archive paths are not supported.");
            return name;
        }
    }
}
