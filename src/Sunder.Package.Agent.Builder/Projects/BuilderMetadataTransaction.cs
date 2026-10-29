using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderMetadataTransaction
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    internal sealed record Change(string Path, string BeforeBase64, string AfterBase64);
    internal sealed record Journal(int SchemaVersion, string EntryPoint, Change[] Files);

    internal static async Task<FileStream> LeaseAsync(string root, CancellationToken token)
    {
        var path = Path.Combine(root, ".sunder-builder-metadata.lock");
        for (var attempt = 0; ; attempt++)
        {
            token.ThrowIfCancellationRequested(); BuilderProjectSource.EnsureSafePath(root, path);
            try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.DeleteOnClose); }
            catch (IOException) when (attempt < 100) { await Task.Delay(50, token).ConfigureAwait(false); }
        }
    }

    internal static async Task CommitAsync(string root, string entryPoint, BuilderProjectSource source, Dictionary<string, byte[]> edits, CancellationToken token)
    {
        var journal = new Journal(1, entryPoint, edits.Select(edit => new Change(edit.Key, Convert.ToBase64String(source.Files[edit.Key].Bytes), Convert.ToBase64String(edit.Value))).ToArray());
        var journalPath = Path.Combine(root, BuilderProjectSource.JournalName);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JsonOptions);
        BuilderProjectSource.EnsureSafePath(root, journalPath);
        await WriteNewAsync(journalPath, bytes, token).ConfigureAwait(false);
        try
        {
            foreach (var change in journal.Files)
            {
                token.ThrowIfCancellationRequested();
                var path = Resolve(root, change.Path);
                if (!(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false)).AsSpan().SequenceEqual(Convert.FromBase64String(change.BeforeBase64)))
                    throw new BuilderProjectConflictException($"Source changed before metadata publication: {change.Path}");
                await ReplaceAsync(root, path, Convert.FromBase64String(change.AfterBase64), token).ConfigureAwait(false);
            }
            foreach (var change in journal.Files)
                if (!(await File.ReadAllBytesAsync(Resolve(root, change.Path), CancellationToken.None).ConfigureAwait(false)).AsSpan().SequenceEqual(Convert.FromBase64String(change.AfterBase64)))
                    throw new BuilderProjectConflictException($"Source changed during metadata publication: {change.Path}");
            // The complete journal is durable before any target changes. Cancellation after this point is a completed save.
            DeleteJournal(root, bytes);
        }
        catch
        {
            await RecoverAsync(root, entryPoint, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task RecoverAsync(string root, string entryPoint, CancellationToken token)
    {
        var path = Path.Combine(root, BuilderProjectSource.JournalName);
        BuilderProjectSource.EnsureSafePath(root, path);
        if (!File.Exists(path)) return;
        if (new FileInfo(path).Length > 64 * 1024 * 1024) throw new InvalidDataException("Metadata recovery journal exceeds its bound.");
        var bytes = await File.ReadAllBytesAsync(path, token).ConfigureAwait(false);
        using var document = BuilderProjectSource.Json(bytes);
        var journal = JsonSerializer.Deserialize<Journal>(bytes, JsonOptions) ?? throw new InvalidDataException("Invalid metadata recovery journal.");
        if (journal.SchemaVersion != 1 || journal.EntryPoint != entryPoint || journal.Files.Length is < 1 or > 128 || journal.Files.Select(file => file.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != journal.Files.Length)
            throw new InvalidDataException("Metadata recovery journal does not describe this project.");
        var changed = new List<(string Path, byte[] Before, byte[] After)>();
        foreach (var file in journal.Files)
        {
            var target = Resolve(root, file.Path);
            if (Path.GetExtension(target) is not (".cs" or ".axaml" or ".ts" or ".tsx" or ".js" or ".jsx" or ".html")
                && file.Path is not ("sunder.package.json" or "global.json" or "package.json" or ".node-version" or ".nvmrc" or ".tool-versions"))
                throw new InvalidDataException("Recovery cannot change baselines, locks, or arbitrary files.");
            var before = Convert.FromBase64String(file.BeforeBase64); var after = Convert.FromBase64String(file.AfterBase64);
            BuilderProjectToolchainUpgrade.ValidatePinChange(file.Path, before, after);
            var current = await File.ReadAllBytesAsync(target, token).ConfigureAwait(false);
            if (current.AsSpan().SequenceEqual(before)) continue;
            if (!current.AsSpan().SequenceEqual(after)) throw new BuilderProjectConflictException($"Recovery preserved external edits to '{file.Path}'. Resolve the journal manually; no files were restored.");
            changed.Add((target, before, after));
        }
        foreach (var file in changed)
        {
            BuilderProjectSource.EnsureSafePath(root, file.Path);
            if (!(await File.ReadAllBytesAsync(file.Path, token).ConfigureAwait(false)).AsSpan().SequenceEqual(file.After)) throw new BuilderProjectConflictException("Source changed during metadata recovery.");
            await ReplaceAsync(root, file.Path, file.Before, token).ConfigureAwait(false);
        }
        DeleteJournal(root, bytes);
    }

    private static string Resolve(string root, string relative)
    {
        if (string.IsNullOrEmpty(relative) || relative.Contains('\\') || relative.Split('/').Any(part => part is "" or "." or ".." || BuilderProjectSource.OutputFolders.Contains(part)) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Unsafe metadata edit path.");
        var path = Path.Combine(root, relative); BuilderProjectSource.EnsureSafePath(root, path); return path;
    }
    private static void DeleteJournal(string root, byte[] expected)
    {
        var path = Path.Combine(root, BuilderProjectSource.JournalName); BuilderProjectSource.EnsureSafePath(root, path);
        if (!File.ReadAllBytes(path).AsSpan().SequenceEqual(expected)) throw new BuilderProjectConflictException("Metadata recovery journal changed; it was preserved.");
        File.Delete(path);
    }
    private static async Task ReplaceAsync(string root, string path, byte[] bytes, CancellationToken token)
    {
        BuilderProjectSource.EnsureSafePath(root, path);
        var stage = Path.Combine(Path.GetDirectoryName(path)!, ".sunder-builder-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            await WriteNewAsync(stage, bytes, token).ConfigureAwait(false);
            BuilderProjectSource.EnsureSafePath(root, path); BuilderProjectSource.EnsureSafePath(root, stage);
            File.Move(stage, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(stage))
            {
                BuilderProjectSource.EnsureSafePath(root, stage);
                if (File.ReadAllBytes(stage).AsSpan().SequenceEqual(bytes)) File.Delete(stage);
            }
        }
    }
    private static async Task WriteNewAsync(string path, byte[] bytes, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(bytes, token).ConfigureAwait(false); stream.Flush(flushToDisk: true);
    }
}
