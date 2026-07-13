using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Shared.Importing;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.Services;

internal sealed record StagedSkillSource(
    string RootPath,
    IReadOnlyList<string> Warnings) : IDisposable
{
    public void Dispose() => SkillImportFileSystem.TryDeleteDirectory(RootPath);
}

internal sealed class SkillSourceAcquirer(
    IGitHubSkillClient gitHubClient,
    IPackageContext packageContext)
{
    internal static readonly ImportTreeLimits Limits = new(
        MaxFiles: 2_048,
        MaxPathDepth: 16,
        MaxFileBytes: 10 * 1024 * 1024,
        MaxTotalBytes: 50 * 1024 * 1024);

    public async Task<StagedSkillSource> AcquireLocalAsync(
        string sourceRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = BoundedImportIO.ValidateTree(sourceRoot, Limits, cancellationToken);
        var selected = files.Where(file => !IsIgnoredPath(file.RelativePath)).ToArray();
        EnsureRootManifest(selected.Select(file => file.RelativePath));
        var stagingRoot = CreateStagingRoot();
        try
        {
            await BoundedImportIO.StageTreeAsync(selected, stagingRoot, cancellationToken).ConfigureAwait(false);
            _ = BoundedImportIO.ValidateTree(stagingRoot, Limits, cancellationToken);
            return new StagedSkillSource(
                stagingRoot,
                selected.Length == files.Count ? [] : ["Skipped source-control metadata while staging the skill."]);
        }
        catch
        {
            SkillImportFileSystem.TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    public async Task<StagedSkillSource> AcquireGitHubAsync(
        GitHubSkillFolder folder,
        CancellationToken cancellationToken)
    {
        var remoteFiles = await gitHubClient.ListFilesAsync(folder, cancellationToken).ConfigureAwait(false);
        var selected = ValidateRemoteFiles(remoteFiles);
        EnsureRootManifest(selected.Select(file => file.RelativePath));
        var stagingRoot = CreateStagingRoot();
        try
        {
            long totalBytes = 0;
            foreach (var file in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var bytes = await gitHubClient.ReadFileAsync(folder, file, cancellationToken).ConfigureAwait(false);
                if (bytes.LongLength > Limits.MaxFileBytes)
                {
                    throw new InvalidDataException($"GitHub skill file is too large: '{file.RelativePath}'.");
                }

                totalBytes = checked(totalBytes + bytes.LongLength);
                if (totalBytes > Limits.MaxTotalBytes)
                {
                    throw new InvalidDataException("GitHub skill folder exceeds the aggregate size limit.");
                }

                var targetPath = Path.Combine(stagingRoot, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                await File.WriteAllBytesAsync(targetPath, bytes, cancellationToken).ConfigureAwait(false);
            }

            _ = BoundedImportIO.ValidateTree(stagingRoot, Limits, cancellationToken);
            return new StagedSkillSource(
                stagingRoot,
                selected.Count == remoteFiles.Count ? [] : ["Skipped source-control metadata while staging the skill."]);
        }
        catch
        {
            SkillImportFileSystem.TryDeleteDirectory(stagingRoot);
            throw;
        }
    }

    private static IReadOnlyList<GitHubSkillFile> ValidateRemoteFiles(IReadOnlyList<GitHubSkillFile> files)
    {
        var selected = new List<GitHubSkillFile>();
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long declaredBytes = 0;
        foreach (var file in files)
        {
            if (paths.Count == Limits.MaxFiles)
            {
                throw new InvalidDataException($"GitHub skill exceeds the {Limits.MaxFiles}-file limit.");
            }

            var relativePath = BoundedImportIO.NormalizeRelativePath(file.RelativePath, Limits.MaxPathDepth);
            if (!paths.TryAdd(relativePath, relativePath))
            {
                throw new InvalidDataException($"GitHub skill contains duplicate or case-colliding path '{relativePath}' and '{paths[relativePath]}'.");
            }

            if (file.Size < 0 || file.Size > Limits.MaxFileBytes)
            {
                throw new InvalidDataException($"GitHub skill file has an invalid size: '{relativePath}'.");
            }

            declaredBytes = checked(declaredBytes + file.Size);
            if (declaredBytes > Limits.MaxTotalBytes)
            {
                throw new InvalidDataException("GitHub skill folder exceeds the aggregate size limit.");
            }

            if (IsIgnoredPath(relativePath))
            {
                continue;
            }

            selected.Add(file with { RelativePath = relativePath });
        }

        return selected.OrderBy(file => file.RelativePath, StringComparer.Ordinal).ToArray();
    }

    private static void EnsureRootManifest(IEnumerable<string> paths)
    {
        if (!paths.Contains("SKILL.md", StringComparer.Ordinal))
        {
            throw new InvalidDataException("Skill folder must contain exactly-cased root SKILL.md file.");
        }
    }

    private string CreateStagingRoot()
    {
        var root = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("skill-import/" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static bool IsIgnoredPath(string relativePath)
        => relativePath.Split('/').Any(segment => segment is ".git" or ".svn" or ".hg");
}

internal static class SkillManifestReader
{
    private const long MaxManifestBytes = 1024 * 1024;

    public static async Task<ParsedSkillMarkdown> ReadAsync(string stagedRoot, CancellationToken cancellationToken)
    {
        var path = Path.Combine(stagedRoot, "SKILL.md");
        var rawContent = await BoundedImportIO.ReadUtf8FileAsync(path, MaxManifestBytes, cancellationToken).ConfigureAwait(false);
        return SkillMarkdownParser.Parse(rawContent);
    }
}

internal sealed record PreparedSkillReplacement(
    string StagingRoot,
    string TargetRoot,
    InstalledSkillRecord Record);

internal sealed class SkillBatchReplacementCommitter(
    SkillStore store,
    Action<SkillImportFaultPoint>? faultInjector)
{
    private const int MaxJournalBytes = 16 * 1024 * 1024;
    private static readonly JsonSerializerOptions JournalJsonOptions = new() { WriteIndented = true };

    public IReadOnlyList<InstalledSkillRecord> Commit(IReadOnlyList<PreparedSkillReplacement> replacements)
    {
        if (replacements.Count == 0)
        {
            return [];
        }

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (replacements.Any(replacement => !ids.Add(replacement.Record.SkillId)))
        {
            throw new InvalidDataException("Skill replacement batch contains duplicate or case-colliding skill ids.");
        }

        using var importTransaction = store.EnterImportTransaction();
        RecoverInterruptedBatch(store);
        var indexSnapshot = store.CaptureIndexSnapshot();
        var journal = new SkillImportJournal(
            "Prepared",
            indexSnapshot is null ? null : Convert.ToBase64String(indexSnapshot),
            replacements.Select(replacement => new SkillImportJournalEntry(
                replacement.StagingRoot,
                replacement.TargetRoot,
                replacement.TargetRoot + ".backup-" + Guid.NewGuid().ToString("N"),
                Directory.Exists(replacement.TargetRoot),
                BackupCreated: false,
                StagedMoved: false)).ToArray());
        WriteJournal(store.ImportJournalPath, journal);
        try
        {
            for (var index = 0; index < replacements.Count; index++)
            {
                var entry = journal.Entries[index];
                if (entry.HadTarget)
                {
                    Directory.Move(entry.TargetRoot, entry.BackupRoot);
                    journal = UpdateEntry(journal, index, entry with { BackupCreated = true });
                    WriteJournal(store.ImportJournalPath, journal);
                }

                faultInjector?.Invoke(SkillImportFaultPoint.AfterBackup);
                Directory.CreateDirectory(Path.GetDirectoryName(entry.TargetRoot)!);
                Directory.Move(entry.StagingRoot, entry.TargetRoot);
                journal = UpdateEntry(journal, index, journal.Entries[index] with { StagedMoved = true });
                WriteJournal(store.ImportJournalPath, journal);
                faultInjector?.Invoke(SkillImportFaultPoint.AfterStagedMove);
            }

            store.SaveSkills(replacements.Select(replacement => replacement.Record).ToArray());
            journal = journal with { Phase = "Committed" };
            WriteJournal(store.ImportJournalPath, journal);
            faultInjector?.Invoke(SkillImportFaultPoint.AfterIndexCommitted);
            TryCompleteCommittedBatch(store.ImportJournalPath, journal);
            store.NotifySkillsImported();
            return replacements.Select(replacement => replacement.Record).ToArray();
        }
        catch (Exception importError)
        {
            var compensationErrors = new List<Exception>();
            try
            {
                RollBackUncommittedBatch(store, journal);
            }
            catch (Exception compensationError)
            {
                compensationErrors.Add(compensationError);
            }

            if (compensationErrors.Count > 0)
            {
                throw new AggregateException("Skill import failed and its prior state could not be fully restored.", [importError, .. compensationErrors]);
            }

            throw;
        }
    }

    internal static void RecoverInterruptedBatch(SkillStore store)
    {
        if (!File.Exists(store.ImportJournalPath))
        {
            return;
        }

        var info = new FileInfo(store.ImportJournalPath);
        if (info.Length > MaxJournalBytes)
        {
            throw new InvalidDataException($"Skill import journal exceeds the {MaxJournalBytes}-byte limit.");
        }

        SkillImportJournal journal;
        try
        {
            journal = JsonSerializer.Deserialize<SkillImportJournal>(File.ReadAllBytes(store.ImportJournalPath), JournalJsonOptions)
                      ?? throw new InvalidDataException("Skill import journal is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException("Skill import journal contains malformed JSON.", ex);
        }

        ValidateJournal(store, journal);
        if (string.Equals(journal.Phase, "Committed", StringComparison.Ordinal))
        {
            CompleteCommittedBatch(store.ImportJournalPath, journal);
            return;
        }

        RollBackUncommittedBatch(store, journal);
    }

    private static void RollBackUncommittedBatch(SkillStore store, SkillImportJournal journal)
    {
        foreach (var entry in journal.Entries.Reverse())
        {
            if (Directory.Exists(entry.BackupRoot))
            {
                SkillImportFileSystem.TryDeleteDirectory(entry.TargetRoot);
                Directory.Move(entry.BackupRoot, entry.TargetRoot);
            }
            else if (!entry.HadTarget && Directory.Exists(entry.TargetRoot))
            {
                Directory.Delete(entry.TargetRoot, recursive: true);
            }

            SkillImportFileSystem.TryDeleteDirectory(entry.StagingRoot);
        }

        store.RestoreIndexSnapshot(journal.IndexSnapshotBase64 is null
            ? null
            : Convert.FromBase64String(journal.IndexSnapshotBase64));
        File.Delete(store.ImportJournalPath);
    }

    private static void CompleteCommittedBatch(string journalPath, SkillImportJournal journal)
    {
        foreach (var entry in journal.Entries)
        {
            if (Directory.Exists(entry.BackupRoot))
            {
                Directory.Delete(entry.BackupRoot, recursive: true);
            }

            if (Directory.Exists(entry.StagingRoot))
            {
                Directory.Delete(entry.StagingRoot, recursive: true);
            }
        }

        File.Delete(journalPath);
    }

    private static void TryCompleteCommittedBatch(string journalPath, SkillImportJournal journal)
    {
        try
        {
            CompleteCommittedBatch(journalPath, journal);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The committed journal remains durable so startup can retry cleanup.
        }
    }

    private static SkillImportJournal UpdateEntry(SkillImportJournal journal, int index, SkillImportJournalEntry entry)
    {
        var entries = journal.Entries.ToArray();
        entries[index] = entry;
        return journal with { Entries = entries };
    }

    private static void WriteJournal(string path, SkillImportJournal journal)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(journal, JournalJsonOptions);
        if (bytes.Length > MaxJournalBytes)
        {
            throw new InvalidDataException($"Skill import journal exceeds the {MaxJournalBytes}-byte limit.");
        }

        var temporaryPath = path + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.WriteThrough))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void ValidateJournal(SkillStore store, SkillImportJournal journal)
    {
        if (journal.Phase is not ("Prepared" or "Committed") || journal.Entries.Count == 0)
        {
            throw new InvalidDataException("Skill import journal has an invalid state.");
        }

        var skillsRoot = Path.GetFullPath(store.SkillsRootPath) + Path.DirectorySeparatorChar;
        var stagingRoot = Path.GetFullPath(store.ImportStagingRootPath) + Path.DirectorySeparatorChar;
        foreach (var entry in journal.Entries)
        {
            var target = Path.GetFullPath(entry.TargetRoot);
            var backup = Path.GetFullPath(entry.BackupRoot);
            var staging = Path.GetFullPath(entry.StagingRoot);
            if (!target.StartsWith(skillsRoot, PathComparison)
                || !backup.StartsWith(skillsRoot, PathComparison)
                || !backup.StartsWith(target + ".backup-", PathComparison)
                || !staging.StartsWith(stagingRoot, PathComparison))
            {
                throw new InvalidDataException("Skill import journal contains a path outside the skill root.");
            }
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
}

internal sealed record SkillImportJournal(
    string Phase,
    string? IndexSnapshotBase64,
    IReadOnlyList<SkillImportJournalEntry> Entries);

internal sealed record SkillImportJournalEntry(
    string StagingRoot,
    string TargetRoot,
    string BackupRoot,
    bool HadTarget,
    bool BackupCreated,
    bool StagedMoved);

internal static class SkillContentHasher
{
    public static async Task<string> ComputeAsync(string rootPath, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            foreach (var file in BoundedImportIO.ValidateTree(rootPath, SkillSourceAcquirer.Limits, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pathBytes = Encoding.UTF8.GetBytes(file.RelativePath);
                hash.AppendData(pathBytes);
                hash.AppendData([0]);
                await using var stream = new FileStream(
                    file.SourcePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    buffer.Length,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                while (true)
                {
                    var read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    hash.AppendData(buffer.AsSpan(0, read));
                }
            }

            return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}

internal static class SkillImportFileSystem
{
    public static void TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
