using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Builder;

// FileShare.None is the existing portable repo pattern (BuilderToolchainLocks,
// SkillStore). The stable gate file is never unlinked, including on disposal.
internal sealed class BuilderJobJournalFiles(IPackageContext packageContext) : IBuilderJobJournalStorage
{
    internal const string DirectoryName = "builder-admission";
    internal const string DocumentName = "journal.v1.json";
    internal const string InitializingMarker = "sunder.builder.admission.gate/v1\ninitializing\n";
    internal const string InitializedMarker = InitializingMarker + "ready\n";
    private const int MaximumFailedTemporaryFiles = 16;

    public async Task<IAsyncDisposable> EnterAsync(CancellationToken token)
    {
        var paths = Paths();
        token.ThrowIfCancellationRequested();
        BuilderToolchainFiles.CreatePrivateDirectory(paths.Directory);
        var started = Stopwatch.GetTimestamp();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            paths = Paths(); // Recheck workspace authority and link containment after each await.
            FileStream gate;
            var created = false;
            try
            {
                try
                {
                    gate = new(paths.Gate, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous);
                    created = true;
                }
                catch (IOException error) when ((error.HResult & 0xffff) is 17 or 80 or 183) // CreateNew collision only.
                { gate = new(paths.Gate, FileMode.Open, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.Asynchronous); }
            }
            // Match the existing BuilderTemplateCache contention codes. Disk,
            // path and permission failures (including IO-wrapped denials) are not
            // lock contention and must surface immediately, not reconnect/retry.
            catch (IOException error) when ((error.HResult & 0xffff) is 11 or 32 or 33 or 35)
            {
                if (Stopwatch.GetElapsedTime(started) >= TimeSpan.FromSeconds(12))
                    throw new IOException("Builder admission did not acquire its stable workspace gate within 12 seconds.", error);
                await Task.Delay(100, token).ConfigureAwait(false);
                continue;
            }
            try
            {
                token.ThrowIfCancellationRequested();
                _ = Paths();
                await InitializeHeldAsync(gate, created, token).ConfigureAwait(false);
                return gate;
            }
            catch { await gate.DisposeAsync().ConfigureAwait(false); throw; }
        }
    }

    public async Task<byte[]?> ReadAsync(CancellationToken token)
        => await ReadDocumentAsync(token).ConfigureAwait(false)
            ?? throw new InvalidDataException("The initialized Builder journal is missing. Its stable gate and other evidence were preserved; absence is not an empty ownership set.");

    private async Task<byte[]?> ReadDocumentAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var paths = Paths();
        FileStream stream;
        try { stream = new(paths.Document, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous); }
        catch (FileNotFoundException) { return null; }
        await using (stream)
        {
            if (stream.Length > BuilderJobJournal.MaximumDocumentBytes)
                throw new InvalidDataException("Builder admission journal exceeds 1 MiB. The original file was preserved.");
            var bytes = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
            if (await stream.ReadAsync(new byte[1], token).ConfigureAwait(false) != 0)
                throw new InvalidDataException("Builder admission journal changed while being read.");
            token.ThrowIfCancellationRequested();
            _ = Paths();
            return bytes;
        }
    }

    private async Task InitializeHeldAsync(FileStream gate, bool created, CancellationToken token)
    {
        var paths = Paths();
        var empty = JsonSerializer.SerializeToUtf8Bytes(BuilderJournalDocument.Empty, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        if (created)
        {
            // CreateNew plus an otherwise empty directory is the only fresh-store
            // proof. A lost gate with surviving document/temporaries is not fresh.
            if (Directory.EnumerateFileSystemEntries(paths.Directory).Any(path => Path.GetFileName(path) != "admission.lock"))
                throw new InvalidDataException("A new Builder gate has pre-existing journal evidence. Initialization was refused without resetting it.");
            await AppendMarkerHeldAsync(gate, InitializingMarker, token).ConfigureAwait(false);
        }
        var marker = await ReadMarkerHeldAsync(gate, token).ConfigureAwait(false);
        if (marker == InitializedMarker) return;
        if (marker != InitializingMarker)
            throw new InvalidDataException("Builder admission gate initialization is incomplete or corrupt. No new-store inference or reset is permitted.");

        // A complete initializing marker is written only before ANY Start can
        // occur. Recover this exact pre-Start crash state, but never overwrite a
        // nonempty, malformed, future, or otherwise different document.
        var document = await ReadDocumentAsync(token).ConfigureAwait(false);
        if (document is null)
        {
            await WriteAsync(empty, token).ConfigureAwait(false);
            document = await ReadDocumentAsync(token).ConfigureAwait(false);
        }
        if (document is null || !document.AsSpan().SequenceEqual(empty))
            throw new InvalidDataException("Builder initialization has unexpected journal data. All evidence was preserved.");
        await AppendMarkerHeldAsync(gate, "ready\n", token).ConfigureAwait(false);
        if (await ReadMarkerHeldAsync(gate, token).ConfigureAwait(false) != InitializedMarker)
            throw new IOException("Builder gate initialization did not acknowledge exact marker readback. Start is forbidden.");
    }

    private async Task AppendMarkerHeldAsync(FileStream gate, string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        _ = Paths();
        gate.Position = gate.Length;
        await gate.WriteAsync(Encoding.UTF8.GetBytes(text), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _ = Paths();
        await gate.FlushAsync(token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _ = Paths();
        gate.Flush(flushToDisk: true);
        token.ThrowIfCancellationRequested();
        _ = Paths();
    }

    private async Task<string> ReadMarkerHeldAsync(FileStream gate, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (gate.Length > 128) throw new InvalidDataException("Builder admission gate marker exceeds its bound.");
        gate.Position = 0;
        var bytes = new byte[checked((int)gate.Length)];
        await gate.ReadExactlyAsync(bytes, token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        _ = Paths();
        return Encoding.UTF8.GetString(bytes);
    }

    public async Task WriteAsync(byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (bytes.Length > BuilderJobJournal.MaximumDocumentBytes) throw new IOException("Builder journal exceeds its 1 MiB write bound.");
        var paths = Paths();
        if (Directory.EnumerateFiles(paths.Directory, ".journal-*.tmp").Take(MaximumFailedTemporaryFiles).Count() >= MaximumFailedTemporaryFiles)
            throw new IOException("Builder admission has too many preserved incomplete writes. Inspect the exact workspace journal before retrying.");
        var temporary = Path.Combine(paths.Directory, ".journal-" + Guid.NewGuid().ToString("N") + ".tmp");
        // Incomplete writes remain as bounded diagnostics. Never run a fresh
        // Delete/reset mutation after cancellation or activation revocation.
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None,
            BufferSize = 1, // No managed write buffer left for disposal to flush after revocation.
            Options = FileOptions.Asynchronous | FileOptions.WriteThrough,
        };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        await using (var stream = new FileStream(temporary, options))
        {
            await stream.WriteAsync(bytes, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _ = Paths();
            await stream.FlushAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            _ = Paths();
            stream.Flush(flushToDisk: true);
        }
        token.ThrowIfCancellationRequested();
        paths = Paths();
        File.Move(temporary, paths.Document, overwrite: true);
        // A failure/cancellation here retains the committed PendingStart. Start
        // is allowed only after the journal owner has acknowledged exact readback.
        token.ThrowIfCancellationRequested();
    }

    private (string Directory, string Document, string Gate) Paths()
    {
        var workspace = packageContext.Storage.RoleLocalWorkspace;
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workspace.WorkspaceRootPath));
        var document = workspace.GetLocalPath(DirectoryName + "/" + DocumentName);
        var gate = workspace.GetLocalPath(DirectoryName + "/admission.lock");
        BuilderToolchainFiles.CheckPath(root, document);
        BuilderToolchainFiles.CheckPath(root, gate);
        return (Path.GetDirectoryName(document)!, document, gate);
    }
}
