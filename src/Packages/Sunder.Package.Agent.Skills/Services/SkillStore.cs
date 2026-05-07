using System.Text.Json;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Skills.Services;

public sealed class SkillStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly object _syncRoot = new();
    private readonly IPackageContext _packageContext;
    private readonly string _indexPath;

    public SkillStore(IPackageContext packageContext)
    {
        _packageContext = packageContext;
        Directory.CreateDirectory(packageContext.Storage.DataRootPath);
        Directory.CreateDirectory(SkillsRootPath);
        _indexPath = Path.Combine(packageContext.Storage.DataRootPath, "skills.json");
    }

    public string SkillsRootPath => _packageContext.Storage.Files.GetPath(SkillConstants.SkillsRelativeRoot);

    public IReadOnlyList<InstalledSkillRecord> ListSkills()
    {
        lock (_syncRoot)
        {
            return LoadIndex()
                .OrderBy(skill => ResolveDisplayName(skill), StringComparer.OrdinalIgnoreCase)
                .ThenBy(skill => skill.SkillId, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public InstalledSkillRecord? GetSkill(string skillId)
    {
        if (string.IsNullOrWhiteSpace(skillId))
        {
            return null;
        }

        lock (_syncRoot)
        {
            return LoadIndex().FirstOrDefault(skill => IsSkillMatch(skill, skillId));
        }
    }

    public string GetSkillRootPath(InstalledSkillRecord skill)
        => _packageContext.Storage.Files.GetPath(skill.RelativeRootPath);

    public string GetSkillMarkdownPath(InstalledSkillRecord skill)
        => Path.Combine(GetSkillRootPath(skill), "SKILL.md");

    public string ReadSkillMarkdown(InstalledSkillRecord skill)
        => File.ReadAllText(GetSkillMarkdownPath(skill));

    public void SaveSkill(InstalledSkillRecord record)
    {
        lock (_syncRoot)
        {
            var skills = LoadIndex()
                .Where(skill => !string.Equals(skill.SkillId, record.SkillId, StringComparison.OrdinalIgnoreCase))
                .Append(record)
                .OrderBy(skill => ResolveDisplayName(skill), StringComparer.OrdinalIgnoreCase)
                .ToArray();
            SaveIndex(skills);
        }
    }

    public bool DeleteSkill(string skillId)
    {
        lock (_syncRoot)
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
            return true;
        }
    }

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
            return JsonSerializer.Deserialize<IReadOnlyList<InstalledSkillRecord>>(File.ReadAllText(_indexPath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void SaveIndex(IReadOnlyList<InstalledSkillRecord> skills)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_indexPath)!);
        File.WriteAllText(_indexPath, JsonSerializer.Serialize(skills, JsonOptions));
    }
}
