using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

internal sealed record BuilderSourceFile(string Path, byte[] Bytes)
{
    internal string Text => BuilderProjectSource.Utf8.GetString(Bytes.AsSpan(HasBom ? 3 : 0));
    internal bool HasBom => Bytes.AsSpan().StartsWith(new byte[] { 0xef, 0xbb, 0xbf });
    internal byte[] Encode(string text) => HasBom ? [0xef, 0xbb, 0xbf, .. BuilderProjectSource.Utf8.GetBytes(text)] : BuilderProjectSource.Utf8.GetBytes(text);
}

internal sealed class BuilderProjectSource
{
    internal static readonly UTF8Encoding Utf8 = new(false, true);
    internal static readonly HashSet<string> OutputFolders = new(StringComparer.OrdinalIgnoreCase)
        { "bin", "obj", "dist", "build", "out", "output", "artifacts", "sunder-dev", ".sunder", ".git", "node_modules", ".vs", ".idea" };
    internal const string JournalName = ".sunder-builder-metadata.transaction.json";
    internal required string Root { get; init; }
    internal required SortedDictionary<string, BuilderSourceFile> Files { get; init; }
    internal string? UnsafeReason { get; init; }
    internal string? InheritedBuildCustomization { get; init; }
    internal bool OutputEvidenceIncomplete { get; init; }
    internal string Revision => Hash(Utf8.GetBytes(string.Join("\n", Files.Select(file => file.Key + "\0" + Hash(file.Value.Bytes))) + "\nimplicit-build:" + InheritedBuildCustomization
        + (OutputEvidenceIncomplete ? "\noutput-evidence:incomplete" : "")));
    internal static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    internal static bool IsEvidence(string name) => name is "sunder-contracts.owned.json" or "authoring-receipt.v1.json"
        or "app-authoring-receipt.v1.json" or "package-export.v1.json" or "owned-baseline.sha256" or "design-time-only.json"
        || name.Contains("identity-fork", StringComparison.OrdinalIgnoreCase)
        || name.Contains("publication", StringComparison.OrdinalIgnoreCase)
        || name.EndsWith(".sunder-generated-output", StringComparison.Ordinal);

    internal static async Task<BuilderProjectSource> CaptureAsync(string root, CancellationToken token,
        Func<string, IEnumerable<string>>? enumerateEntries = null)
    {
        root = Path.GetFullPath(root);
        EnsureSafePath(root, root);
        var files = new SortedDictionary<string, BuilderSourceFile>(StringComparer.Ordinal);
        string? unsafeReason = null;
        var outputEvidenceIncomplete = false;
        void OutputChanged(string path)
        {
            // Discovery/build observation can use authored identity, but missing
            // generated history must never grant metadata mutation permission.
            outputEvidenceIncomplete = true;
            unsafeReason ??= $"Generated source/evidence '{Path.GetRelativePath(root, path)}' changed during inspection. Reload metadata after the build settles.";
        }
        var pending = new Stack<(string Path, int Depth, bool Output)>();
        pending.Push((root, 0, false));
        var count = 0;
        long size = 0;
        while (pending.TryPop(out var directory))
        {
            token.ThrowIfCancellationRequested();
            string[] entries;
            try { entries = (enumerateEntries ?? Directory.EnumerateFileSystemEntries)(directory.Path).Take(32769 - count).Order(StringComparer.Ordinal).ToArray(); }
            catch (Exception error) when (directory.Output && error is FileNotFoundException or DirectoryNotFoundException)
            { OutputChanged(directory.Path); continue; }
            foreach (var path in entries)
            {
                if (++count > 32768 || directory.Depth > 32) throw new InvalidDataException("Project inspection exceeds 32 levels or 32,768 entries. Open a narrower project folder.");
                var name = Path.GetFileName(path);
                if (name is ".git" or "node_modules" or ".vs" or ".idea" || name.StartsWith(".sunder-builder-", StringComparison.Ordinal) && name != JournalName) continue;
                try
                {
                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                    {
                        if (!directory.Output || IsEvidence(name) || (attributes & FileAttributes.Directory) != 0) unsafeReason = $"Linked source/evidence '{Path.GetRelativePath(root, path)}' is outside the bounded editor. Use regular in-project source files.";
                        continue;
                    }
                    if ((attributes & FileAttributes.Directory) != 0)
                    {
                        // Nested packages are independent candidates, not this project's source.
                        if (!directory.Output && !OutputFolders.Contains(name) && Directory.EnumerateFiles(path).Take(32769 - count).Any(file => file.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(file) == "sunder.package.json")) continue;
                        pending.Push((path, directory.Depth + 1, directory.Output || OutputFolders.Contains(name)));
                        continue;
                    }
                    var extension = Path.GetExtension(name);
                    var source = !directory.Output && (extension is ".cs" or ".csproj" or ".props" or ".targets" or ".axaml" or ".ts" or ".tsx" or ".js" or ".jsx" or ".html"
                        || name.StartsWith("sunder", StringComparison.Ordinal) && extension == ".json" || name is "package.json" or "tsconfig.json" or "global.json" or ".node-version" or ".nvmrc" or ".tool-versions" or JournalName);
                    if (!source && !IsEvidence(name)) continue;
                    EnsureSafePath(root, path);
                    var info = new FileInfo(path);
                    if (info.Length > 16 * 1024 * 1024 || (size += info.Length) > 64 * 1024 * 1024) throw new InvalidDataException("Project source/evidence exceeds the bounded 16 MiB file or 64 MiB total limit.");
                    var bytes = await ReadBoundedAsync(root, path, info.Length, token).ConfigureAwait(false);
                    files.Add(Path.GetRelativePath(root, path).Replace('\\', '/'), new(path, bytes));
                }
                catch (Exception error) when ((directory.Output || OutputFolders.Contains(name)) && error is FileNotFoundException or DirectoryNotFoundException)
                { OutputChanged(path); }
            }
        }
        if (files.ContainsKey(JournalName)) unsafeReason = "An interrupted metadata save has recovery data. Call RecoverAsync before editing.";
        string? inherited = null;
        for (var directory = root; !string.IsNullOrEmpty(directory) && inherited is null; directory = Path.GetDirectoryName(directory))
            foreach (var name in new[] { "Directory.Build.props", "Directory.Build.targets" })
                if (File.Exists(Path.Combine(directory, name))) { inherited = Path.Combine(directory, name); break; }
        return new() { Root = root, Files = files, UnsafeReason = unsafeReason, InheritedBuildCustomization = inherited, OutputEvidenceIncomplete = outputEvidenceIncomplete };
    }

    private static async Task<byte[]> ReadBoundedAsync(string root, string path, long expectedLength, CancellationToken token)
    {
        var before = File.GetLastWriteTimeUtc(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        if (stream.Length != expectedLength) throw new BuilderProjectConflictException($"Source changed during inspection: {path}");
        var bytes = new byte[checked((int)expectedLength)];
        await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        EnsureSafePath(root, path);
        if (stream.Length != expectedLength || new FileInfo(path).Length != expectedLength || File.GetLastWriteTimeUtc(path) != before)
            throw new BuilderProjectConflictException($"Source changed during inspection: {path}");
        return bytes;
    }

    internal static void EnsureSafePath(string root, string path)
        => BuilderProjectFiles.EnsureSafePath(root, path);

    internal static JsonDocument Json(byte[] bytes)
        => BuilderProjectFiles.Json(bytes);

    internal static void ValidateName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 256 || value.Trim() != value || value.Any(char.IsControl) || value.Any(char.IsSurrogate) && !ValidUnicode(value))
            throw new ArgumentException("Display name must be trimmed, nonempty, control-free Unicode of at most 256 characters.", nameof(value));
    }
    private static bool ValidUnicode(string value) { try { _ = Utf8.GetBytes(value); return true; } catch (EncoderFallbackException) { return false; } }
    internal static void ValidateId(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 128 || value.Split('.').Any(segment => segment.Length == 0 || segment.Any(character => character is not (>= 'a' and <= 'z' or >= '0' and <= '9'))))
            throw new ArgumentException("Package and derived IDs must be lowercase dot-separated ASCII letters/digits of at most 128 characters.", nameof(value));
    }
}
