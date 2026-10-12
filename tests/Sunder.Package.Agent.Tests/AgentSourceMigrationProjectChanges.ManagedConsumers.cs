using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // These inverse source deltas preserve the authenticated pre-cutover inputs.
    // They are data for the frozen oracle, never compiled package implementations.
    private static readonly Lazy<IReadOnlyDictionary<string, OrdinarySourceDelta>> ManagedConsumerSourceHistory = new(() =>
    {
        var text = ReadNormalizedProjectText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
            "tests/Sunder.Package.Agent.Tests/Fixtures/ManagedConsumers.source-history.json"));
        AssertProjectHash("e985385f765146b928987f7264b1ad554d6f8680f8c54459bcd1895c21070d8d", text, "Managed consumer source history");
        var history = JsonSerializer.Deserialize<OrdinarySourceHistoryRecord>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(12, history.Sources.Length);
        return history.Sources.ToDictionary(source => source.Path, StringComparer.Ordinal);
    });

    private static string BeforeManagedConsumerServices(string relative, string current)
    {
        var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
        if (TryReadNaturalServiceDelta(root, Path.Combine(root, relative), current, out var preNatural)) return preNatural;
        if (!ManagedConsumerSourceHistory.Value.TryGetValue(relative, out var source)) return current;
        AssertProjectHash(source.CurrentSha256, current, relative + " (managed consumer source)");
        var split = current.Split('\n');
        var lines = split.Select((line, index) => index < split.Length - 1 ? line + "\n" : line).ToList();
        foreach (var edit in source.Edits.Reverse())
        {
            Assert.InRange(edit.StartLine, 0, lines.Count);
            Assert.InRange(edit.RemoveLineCount, 0, lines.Count - edit.StartLine);
            lines.RemoveRange(edit.StartLine, edit.RemoveLineCount);
            lines.InsertRange(edit.StartLine, edit.BeforeLines);
        }
        var before = string.Concat(lines);
        AssertProjectHash(source.BeforeSha256, before, relative + " (pre-managed consumer source)");
        return before;
    }
}
