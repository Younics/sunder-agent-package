using System.Text.Json;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

public sealed class SubagentStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _syncRoot = new();
    private readonly string _filePath;

    public SubagentStore(IPackageContext context)
    {
        Directory.CreateDirectory(context.Storage.DataRootPath);
        _filePath = Path.Combine(context.Storage.DataRootPath, "subagents.json");
    }

    public IReadOnlyList<SubagentRecord> List()
    {
        lock (_syncRoot)
        {
            return ReadAll()
                .OrderBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
    }

    public SubagentRecord? Get(string subagentId)
    {
        if (string.IsNullOrWhiteSpace(subagentId))
        {
            return null;
        }

        lock (_syncRoot)
        {
            return ReadAll().FirstOrDefault(agent => string.Equals(agent.SubagentId, subagentId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public SubagentRecord Save(SubagentRecord record)
    {
        lock (_syncRoot)
        {
            var items = ReadAll().Where(agent => !string.Equals(agent.SubagentId, record.SubagentId, StringComparison.OrdinalIgnoreCase)).ToList();
            items.Add(record);
            WriteAll(items);
            return record;
        }
    }

    public void Delete(string subagentId)
    {
        lock (_syncRoot)
        {
            WriteAll(ReadAll().Where(agent => !string.Equals(agent.SubagentId, subagentId, StringComparison.OrdinalIgnoreCase)).ToArray());
        }
    }

    private IReadOnlyList<SubagentRecord> ReadAll()
    {
        if (!File.Exists(_filePath))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<IReadOnlyList<SubagentRecord>>(File.ReadAllText(_filePath), JsonOptions) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private void WriteAll(IReadOnlyList<SubagentRecord> records)
        => File.WriteAllText(_filePath, JsonSerializer.Serialize(records, JsonOptions));
}
