using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.Services;

internal static class SkillPersistenceSchemas
{
    public const string IndexId = "sunder.agent.skills.index";
    public const string ImportJournalId = "sunder.agent.skills.import-journal";
    public const int CurrentVersion = 1;
}

internal static class SkillPersistenceJson
{
    public static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidDataException($"Skill persistence document contains duplicate property '{property.Name}'.");
                }

                RejectDuplicateProperties(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                RejectDuplicateProperties(item);
            }
        }
    }
}

public sealed class SkillStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private const int MaxIndexBytes = 4 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SharedLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _syncRoot;
    private readonly IPackageContext _packageContext;
    private readonly string _indexPath;
    private readonly string _lockPath;
    private int _initialized;

    public SkillStore(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        _indexPath = packageContext.Storage.RoleLocalWorkspace.GetLocalPath("skills/skills.json");
        _lockPath = _indexPath + ".lock";
        _syncRoot = SharedLocks.GetOrAdd(_indexPath, static _ => new object());
    }

    public void Activate() => EnsureInitialized();

    public string SkillsRootPath => _packageContext.Storage.RoleLocalWorkspace.GetLocalPath(SkillConstants.SkillsRelativeRoot);

    public event Action? SkillsChanged;

    public IReadOnlyList<InstalledSkillRecord> ListSkills()
    {
        using var transaction = EnterTransaction();
        return LoadIndex()
            .OrderBy(skill => ResolveDisplayName(skill), StringComparer.OrdinalIgnoreCase)
            .ThenBy(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public InstalledSkillRecord? GetSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            return null;
        }

        using var transaction = EnterTransaction();
        return LoadIndex().FirstOrDefault(skill => IsSkillMatch(skill, skillId));
    }

    public string GetSkillRootPath(InstalledSkillRecord skill)
    {
        ValidateRecord(skill);
        var root = Path.GetFullPath(_packageContext.Storage.RoleLocalWorkspace.GetLocalPath(skill.RelativeRootPath));
        var skillsRoot = Path.GetFullPath(SkillsRootPath) + Path.DirectorySeparatorChar;
        if (!root.StartsWith(skillsRoot, PathComparison))
        {
            throw new InvalidDataException($"Skill '{skill.SkillId}' has a storage path outside the skill root.");
        }

        return root;
    }

    public string GetSkillMarkdownPath(InstalledSkillRecord skill)
        => Path.Combine(GetSkillRootPath(skill), "SKILL.md");

    public string ReadSkillMarkdown(InstalledSkillRecord skill)
        => File.ReadAllText(GetSkillMarkdownPath(skill));

    public void SaveSkill(InstalledSkillRecord record)
    {
        using (EnterTransaction())
        {
            var skills = LoadIndex()
                .Where(skill => !string.Equals(skill.SkillId, record.SkillId, StringComparison.OrdinalIgnoreCase))
                .Append(record)
                .OrderBy(skill => ResolveDisplayName(skill), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SaveIndex(skills);
        }

        SkillsChanged?.Invoke();
    }

    internal byte[]? CaptureIndexSnapshot()
    {
        using var transaction = EnterTransaction();
        return File.Exists(_indexPath) ? File.ReadAllBytes(_indexPath) : null;
    }

    internal IDisposable EnterImportTransaction()
        => EnterTransaction();

    internal string IndexPath => _indexPath;

    internal string ImportJournalPath => _indexPath + ".import-journal";

    internal string ImportStagingRootPath => _packageContext.Storage.RoleLocalWorkspace.GetLocalPath("skill-import");

    internal void RestoreIndexSnapshot(byte[]? snapshot)
    {
        using (EnterTransaction())
        {
            if (snapshot is null)
            {
                File.Delete(_indexPath);
                return;
            }

            WriteIndex(snapshot);
        }
    }

    internal void SaveSkills(IReadOnlyList<InstalledSkillRecord> records)
    {
        using (EnterTransaction())
        {
            var replacedIds = records.Select(record => record.SkillId).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var skills = LoadIndex()
                .Where(skill => !replacedIds.Contains(skill.SkillId))
                .Concat(records)
                .OrderBy(skill => ResolveDisplayName(skill), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SaveIndex(skills);
        }
    }

    public bool DeleteSkill(string skillId)
    {
        using (EnterTransaction())
        {
            var skills = LoadIndex();
            var skill = skills.FirstOrDefault(item => IsSkillMatch(item, skillId));
            if (skill is null)
            {
                return false;
            }

            var root = GetSkillRootPath(skill);
            var backupRoot = root + ".delete-" + Guid.NewGuid().ToString("N");
            var moved = false;
            try
            {
                if (Directory.Exists(root))
                {
                    Directory.Move(root, backupRoot);
                    moved = true;
                }

                SaveIndex(skills.Where(item => !string.Equals(item.SkillId, skill.SkillId, StringComparison.OrdinalIgnoreCase)).ToArray());
            }
            catch
            {
                if (moved && Directory.Exists(backupRoot) && !Directory.Exists(root))
                {
                    Directory.Move(backupRoot, root);
                }

                throw;
            }

            SkillImportFileSystem.TryDeleteDirectory(backupRoot);
        }

        SkillsChanged?.Invoke();
        return true;
    }

    public void NotifySkillsImported()
        => SkillsChanged?.Invoke();

    public static string ResolveDisplayName(InstalledSkillRecord skill)
        => string.IsNullOrWhiteSpace(skill.Name) ? skill.SkillId : skill.Name.Trim();

    private static bool IsSkillMatch(InstalledSkillRecord skill, string value)
        => string.Equals(skill.SkillId, value, StringComparison.OrdinalIgnoreCase)
           || (!string.IsNullOrWhiteSpace(skill.Name)
               && string.Equals(skill.Name, value, StringComparison.OrdinalIgnoreCase));

    private IReadOnlyList<InstalledSkillRecord> LoadIndex()
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }

        try
        {
            var info = new FileInfo(_indexPath);
            if (info.Length > MaxIndexBytes)
            {
                throw new InvalidDataException($"The skill index '{_indexPath}' exceeds the {MaxIndexBytes}-byte limit.");
            }

            IReadOnlyList<InstalledSkillRecord> records;
            try
            {
                var bytes = File.ReadAllBytes(_indexPath);
                if (bytes.Length > MaxIndexBytes)
                {
                    throw new InvalidDataException($"The skill index '{_indexPath}' grew beyond the {MaxIndexBytes}-byte limit while it was being read.");
                }

                var json = StrictUtf8.GetString(bytes);
                using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 64 });
                SkillPersistenceJson.RejectDuplicateProperties(document.RootElement);
                records = document.RootElement.ValueKind switch
                {
                    JsonValueKind.Array => JsonSerializer.Deserialize<IReadOnlyList<InstalledSkillRecord>>(json, JsonOptions),
                    JsonValueKind.Object => ReadCurrentIndex(json, document.RootElement),
                    _ => null,
                } ?? throw new InvalidDataException($"The skill index '{_indexPath}' is empty or has an unsupported shape.");
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidDataException($"The skill index '{_indexPath}' is not valid UTF-8.", ex);
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var record in records)
            {
                ValidateRecord(record);
                if (!ids.Add(record.SkillId))
                {
                    throw new InvalidDataException($"The skill index contains duplicate or case-colliding id '{record.SkillId}'.");
                }
            }

            return records;
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The skill index '{_indexPath}' contains malformed JSON.", ex);
        }
    }

    private void SaveIndex(IReadOnlyList<InstalledSkillRecord> skills)
    {
        foreach (var skill in skills)
        {
            ValidateRecord(skill);
        }

        WriteIndex(JsonSerializer.SerializeToUtf8Bytes(
            new SkillIndexDocument(
                SkillPersistenceSchemas.IndexId,
                SkillPersistenceSchemas.CurrentVersion,
                skills),
            JsonOptions));
    }

    private InvalidDataException UnsupportedIndexSchema()
        => new($"The skill index '{_indexPath}' has an unsupported or missing schema identity.");

    private IReadOnlyList<InstalledSkillRecord> ReadCurrentIndex(string json, JsonElement root)
    {
        if (!root.TryGetProperty(nameof(SkillIndexDocument.SchemaId), out var schemaId)
            || schemaId.ValueKind != JsonValueKind.String
            || !string.Equals(schemaId.GetString(), SkillPersistenceSchemas.IndexId, StringComparison.Ordinal)
            || !root.TryGetProperty(nameof(SkillIndexDocument.SchemaVersion), out var schemaVersion)
            || !schemaVersion.TryGetInt32(out var version)
            || version != SkillPersistenceSchemas.CurrentVersion)
        {
            throw UnsupportedIndexSchema();
        }

        return JsonSerializer.Deserialize<SkillIndexDocument>(json, JsonOptions)?.Skills
               ?? throw new InvalidDataException($"The skill index '{_indexPath}' is empty.");
    }

    private static void ValidateRecord(InstalledSkillRecord skill)
    {
        if (string.IsNullOrWhiteSpace(skill.SkillId)
            || skill.SkillId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || skill.SkillId is "." or ".."
            || skill.SkillId.Contains(Path.DirectorySeparatorChar)
            || skill.SkillId.Contains(Path.AltDirectorySeparatorChar))
        {
            throw new InvalidDataException("The skill index contains an invalid skill id.");
        }

        var expectedRoot = SkillConstants.SkillsRelativeRoot + "/" + skill.SkillId;
        if (!string.Equals(skill.RelativeRootPath.Replace('\\', '/'), expectedRoot, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Skill '{skill.SkillId}' has an invalid storage path.");
        }
    }

    private static StringComparison PathComparison
        => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

    private void WriteIndex(byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        var temporaryPath = _indexPath + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _indexPath, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private IDisposable EnterTransaction()
    {
        EnsureInitialized();
        var ownsProcessLock = !Monitor.IsEntered(_syncRoot);
        Monitor.Enter(_syncRoot);
        try
        {
            return new TransactionLease(
                _syncRoot,
                ownsProcessLock ? AcquireProcessLock() : null);
        }
        catch
        {
            Monitor.Exit(_syncRoot);
            throw;
        }
    }

    private void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 2)
        {
            return;
        }

        lock (_syncRoot)
        {
            if (_initialized == 2)
            {
                return;
            }

            Directory.CreateDirectory(SkillsRootPath);
            Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
            _initialized = 2;
            try
            {
                using var processLock = AcquireProcessLock();
                SkillBatchReplacementCommitter.RecoverInterruptedBatch(this);
            }
            catch
            {
                _initialized = 0;
                throw;
            }
        }
    }

    private FileStream AcquireProcessLock()
    {
        var startedAt = DateTime.UtcNow;
        IOException? lastError = null;
        while (DateTime.UtcNow - startedAt < LockTimeout)
        {
            try
            {
                return new FileStream(_lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                lastError = ex;
                Thread.Sleep(10);
            }
        }

        throw new IOException($"Timed out waiting for exclusive access to the skill index '{_indexPath}'.", lastError);
    }

    private sealed class TransactionLease(object syncRoot, FileStream? processLock) : IDisposable
    {
        private object? _syncRoot = syncRoot;
        private FileStream? _processLock = processLock;

        public void Dispose()
        {
            var syncRoot = Interlocked.Exchange(ref _syncRoot, null);
            if (syncRoot is not null)
            {
                Interlocked.Exchange(ref _processLock, null)?.Dispose();
                Monitor.Exit(syncRoot);
            }
        }
    }
}

internal sealed record SkillIndexDocument(
    string SchemaId,
    int SchemaVersion,
    IReadOnlyList<InstalledSkillRecord> Skills);
