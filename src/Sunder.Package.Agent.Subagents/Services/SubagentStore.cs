using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Sunder.Package.Agent.Subagents.Models;
using Sunder.Sdk.Abstractions;

namespace Sunder.Package.Agent.Subagents.Services;

public sealed class SubagentStore
{
    private const int CurrentVersion = 1;
    private static readonly TimeSpan LockTimeout = TimeSpan.FromSeconds(30);
    private static readonly ConcurrentDictionary<string, object> PathLocks = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private readonly object _syncRoot;
    private readonly string _filePath;
    private readonly string _lockPath;

    public SubagentStore(IPackageContext context)
    {
        _filePath = context.Storage.LocalWorkspace.GetLocalPath("subagents/subagents.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_filePath)!);
        _lockPath = _filePath + ".lock";
        _syncRoot = PathLocks.GetOrAdd(_filePath, static _ => new object());
    }

    public IReadOnlyList<SubagentRecord> List()
    {
        using var transaction = EnterTransaction();
        return ReadDocument().Records
            .OrderBy(agent => agent.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public SubagentRecord? Get(string subagentId)
    {
        if (string.IsNullOrWhiteSpace(subagentId))
        {
            return null;
        }

        using var transaction = EnterTransaction();
        return ReadDocument().Records.FirstOrDefault(agent =>
            string.Equals(agent.SubagentId, subagentId, StringComparison.OrdinalIgnoreCase));
    }

    public SubagentRecord Save(SubagentRecord record)
        => SaveMany([record])[0];

    public IReadOnlyList<SubagentRecord> SaveMany(IReadOnlyList<SubagentRecord> records)
    {
        ValidateRecords(records, "save request");
        if (records.Count == 0)
        {
            return [];
        }

        using (EnterTransaction())
        {
            var document = ReadDocument();
            foreach (var record in records)
            {
                UpsertRecord(document.Items, record);
            }

            WriteDocument(document.Root);
            return records.ToArray();
        }
    }

    public void Delete(string subagentId)
    {
        using (EnterTransaction())
        {
            var document = ReadDocument();
            for (var index = document.Items.Count - 1; index >= 0; index--)
            {
                var record = DeserializeRecord(document.Items[index], index);
                if (string.Equals(record.SubagentId, subagentId, StringComparison.OrdinalIgnoreCase))
                {
                    document.Items.RemoveAt(index);
                }
            }

            WriteDocument(document.Root);
        }
    }

    private StoreDocument ReadDocument()
    {
        if (!File.Exists(_filePath))
        {
            return CreateEmptyDocument();
        }

        JsonNode rootNode;
        try
        {
            rootNode = JsonNode.Parse(
                File.ReadAllText(_filePath),
                new JsonNodeOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("The subagent store is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"The subagent store '{_filePath}' contains malformed JSON.", ex);
        }

        JsonObject root;
        JsonArray items;
        if (rootNode is JsonArray legacyItems)
        {
            var migrated = CreateEmptyDocument();
            foreach (var item in legacyItems)
            {
                migrated.Items.Add(item?.DeepClone());
            }

            root = migrated.Root;
            items = migrated.Items;
        }
        else if (rootNode is JsonObject versionedRoot)
        {
            root = versionedRoot;
            var version = ReadVersion(root);
            if (version > CurrentVersion)
            {
                throw new NotSupportedException(
                    $"The subagent store uses version {version}, but this runtime supports up to version {CurrentVersion}. The file was not modified.");
            }

            if (version != CurrentVersion || root["subagents"] is not JsonArray storedItems)
            {
                throw new InvalidDataException(
                    $"The subagent store '{_filePath}' does not contain a valid version {CurrentVersion} document.");
            }

            items = storedItems;
        }
        else
        {
            throw new InvalidDataException($"The subagent store '{_filePath}' must contain an object or a legacy array.");
        }

        var records = items.Select(DeserializeRecord).ToArray();
        ValidateRecords(records, "store");
        return new StoreDocument(root, items, records);
    }

    private void WriteDocument(JsonObject root)
    {
        root["version"] = CurrentVersion;
        var temporaryPath = $"{_filePath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = new FileStream(
                       temporaryPath,
                       FileMode.CreateNew,
                       FileAccess.Write,
                       FileShare.None,
                       64 * 1024,
                       FileOptions.WriteThrough))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
            {
                writer.Write(root.ToJsonString(JsonOptions));
                writer.Flush();
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, _filePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private IDisposable EnterTransaction()
    {
        Monitor.Enter(_syncRoot);
        try
        {
            return new TransactionLease(_syncRoot, AcquireProcessLock());
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

        throw new IOException($"Timed out waiting for exclusive access to the subagent store '{_filePath}'.", lastError);
    }

    private static StoreDocument CreateEmptyDocument()
    {
        var items = new JsonArray();
        var root = new JsonObject
        {
            ["version"] = CurrentVersion,
            ["subagents"] = items,
        };
        return new StoreDocument(root, items, []);
    }

    private static int ReadVersion(JsonObject root)
    {
        try
        {
            return root["version"]?.GetValue<int>()
                   ?? throw new InvalidDataException("The versioned subagent store is missing its version.");
        }
        catch (InvalidOperationException ex)
        {
            throw new InvalidDataException("The subagent store version must be an integer.", ex);
        }
    }

    private static SubagentRecord DeserializeRecord(JsonNode? node, int index)
    {
        if (node is not JsonObject)
        {
            throw new InvalidDataException($"Subagent store item {index} is not an object.");
        }

        try
        {
            return node.Deserialize<SubagentRecord>(JsonOptions)
                   ?? throw new InvalidDataException($"Subagent store item {index} is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Subagent store item {index} is malformed.", ex);
        }
    }

    private static void ValidateRecords(IReadOnlyList<SubagentRecord> records, string source)
    {
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < records.Count; index++)
        {
            var record = records[index];
            if (string.IsNullOrWhiteSpace(record.SubagentId) || string.IsNullOrWhiteSpace(record.DisplayName))
            {
                throw new InvalidDataException($"Subagent {source} item {index} is missing its id or display name.");
            }

            if (!ids.Add(record.SubagentId))
            {
                throw new InvalidDataException($"Subagent {source} contains duplicate id '{record.SubagentId}'.");
            }
        }
    }

    private static void UpsertRecord(JsonArray items, SubagentRecord record)
    {
        JsonObject? target = null;
        for (var index = 0; index < items.Count; index++)
        {
            var existing = DeserializeRecord(items[index], index);
            if (string.Equals(existing.SubagentId, record.SubagentId, StringComparison.OrdinalIgnoreCase))
            {
                target = (JsonObject)items[index]!;
                break;
            }
        }

        var serialized = JsonSerializer.SerializeToNode(record, JsonOptions) as JsonObject
                         ?? throw new InvalidOperationException("The subagent record could not be serialized.");
        if (target is null)
        {
            items.Add(serialized);
            return;
        }

        MergeKnownProperties(target, serialized);
    }

    private static void MergeKnownProperties(JsonObject target, JsonObject source)
    {
        foreach (var property in source)
        {
            if (property.Value is JsonObject sourceObject && target[property.Key] is JsonObject targetObject)
            {
                MergeKnownProperties(targetObject, sourceObject);
            }
            else if (property.Value is JsonArray sourceArray && target[property.Key] is JsonArray targetArray)
            {
                MergeKnownItems(targetArray, sourceArray);
            }
            else
            {
                target[property.Key] = property.Value?.DeepClone();
            }
        }
    }

    private static void MergeKnownItems(JsonArray target, JsonArray source)
    {
        while (target.Count > source.Count)
        {
            target.RemoveAt(target.Count - 1);
        }

        for (var index = 0; index < source.Count; index++)
        {
            if (index >= target.Count)
            {
                target.Add(source[index]?.DeepClone());
            }
            else if (source[index] is JsonObject sourceObject && target[index] is JsonObject targetObject)
            {
                MergeKnownProperties(targetObject, sourceObject);
            }
            else
            {
                target[index] = source[index]?.DeepClone();
            }
        }
    }

    private sealed record StoreDocument(
        JsonObject Root,
        JsonArray Items,
        IReadOnlyList<SubagentRecord> Records);

    private sealed class TransactionLease(object syncRoot, FileStream processLock) : IDisposable
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
