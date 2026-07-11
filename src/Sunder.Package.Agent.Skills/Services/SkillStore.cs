using System.Collections.Concurrent;
using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.Services;

public sealed class SkillStore
{
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly ConcurrentDictionary<string, object> SharedLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly object _syncRoot;
    private readonly IPackageContext _packageContext;
    private readonly string _indexPath;
    private readonly string _lockPath;

    public SkillStore(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        Directory.CreateDirectory(SkillsRootPath);
        _indexPath = packageContext.Storage.LocalWorkspace.GetLocalPath("skills/skills.json");
        _lockPath = _indexPath + ".lock";
        _syncRoot = SharedLocks.GetOrAdd(_indexPath, static _ => new object());
    }

    public string SkillsRootPath => _packageContext.Storage.LocalWorkspace.GetLocalPath(SkillConstants.SkillsRelativeRoot);

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
        => _packageContext.Storage.LocalWorkspace.GetLocalPath(skill.RelativeRootPath);

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
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            SaveIndex(skills.Where(item => !string.Equals(item.SkillId, skill.SkillId, StringComparison.OrdinalIgnoreCase)).ToArray());
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
            return JsonSerializer.Deserialize<IReadOnlyList<InstalledSkillRecord>>(File.ReadAllText(_indexPath), JsonOptions)
                   ?? throw new InvalidDataException($"The skill index '{_indexPath}' is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The skill index '{_indexPath}' contains malformed JSON.", ex);
        }
    }

    private void SaveIndex(IReadOnlyList<InstalledSkillRecord> skills)
        => WriteIndex(JsonSerializer.SerializeToUtf8Bytes(skills, JsonOptions));

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
