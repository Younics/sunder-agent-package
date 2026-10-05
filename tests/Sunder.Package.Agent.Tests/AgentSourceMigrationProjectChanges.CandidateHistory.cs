using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal const string HistoricalCandidateSha256 = "0c01e196c11a344981e44b486d1f6b065ed677344611cb9b5da7cee35429959e";

    internal static IReadOnlyDictionary<string, CandidateInputCapture> ReadCandidateInputHistory(string root)
    {
        var text = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, "candidate-inputs-history.json"));
        AssertProjectHash("8d8c034e0c0d2650b7a1ef043ca057678e948f3ae371e3fda12e922a6f584cfc", text, "candidate-inputs-history.json");
        var history = JsonSerializer.Deserialize<CandidateInputHistory>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        Assert.Equal(HistoricalCandidateSha256, history.CandidateSha256);
        Assert.Equal(43, history.Entries.Length);
        Assert.Equal(41, history.Entries.Count(entry => entry.Path.EndsWith("/packages.release.lock.json", StringComparison.Ordinal)));
        foreach (var entry in history.Entries)
        {
            // Preserve the original bytes, including any original line endings:
            // this candidate used raw file hashes, unlike the RPC source oracle.
            AssertProjectHash(entry.Sha256, entry.Text, entry.Path + " (candidate input)");
            Assert.StartsWith("git-index:", entry.Provenance, StringComparison.Ordinal);
        }
        return history.Entries.ToDictionary(entry => entry.Path, StringComparer.Ordinal);
    }

    internal sealed record CandidateInputCapture(string Path, string Sha256, string Provenance, string Text);
    private sealed record CandidateInputHistory(int SchemaVersion, string CandidateSha256, CandidateInputCapture[] Entries);
}
