using System.Text.Json;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static IReadOnlyList<ApprovedTextChange> ReadPhysicalCleanupHistory(string root)
        => ReadApprovedTextChanges(root, "physical-cleanup-history.json", "2e3faefea55a39d04e8d1c9fc839286f472e24442ba4cbafc1d7f9c4846c504e");

    internal static IReadOnlyList<ApprovedTextChange> ReadUiFollowupHistory(string root)
        => ReadApprovedTextChanges(root, "ui-followup-history.json", "7dfd90a1bd145160157ecb354baf342a699e566f2eb7fc36a0c7a811862b81cf");

    private static IReadOnlyList<ApprovedTextChange> ReadApprovedTextChanges(string root, string file, string sha256)
    {
        var text = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, file));
        AssertProjectHash(sha256, text, file);
        var history = JsonSerializer.Deserialize<ApprovedTextHistory>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(1, history.SchemaVersion);
        foreach (var change in history.Changes)
        {
            AssertProjectHash(change.BeforeSha256, change.BeforeText, change.Path + " (authentic before-image)");
            Assert.NotEmpty(change.Provenance);
        }
        return history.Changes;
    }

    internal static string BeforeUiFollowup(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/src/Sunder.Package.Agent.Shared/PackageViews/TranscriptScrollCoordinator.cs", StringComparison.Ordinal))
        {
            // The forced jump now retains authority through the existing bounded
            // layout-settling path. Invert only the exact approved addition.
            const string addition = "            if (request.Force && IsFollowingTail)\n"
                + "            {\n"
                + "                // A forced jump can realize/reflow the tail after this first\n"
                + "                // offset write. Keep its authority through the bounded layout\n"
                + "                // settling path so native tail anchoring has a visible target.\n"
                + "                QueueScrollToBottomAfterLayoutSettles();\n"
                + "            }\n";
            Assert.Equal(2, text.Split(addition, StringSplitOptions.None).Length);
            return text.Replace(addition, "", StringComparison.Ordinal);
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent.Subagents/PackageViews/SubagentsViewModel.Operations.cs", StringComparison.Ordinal))
        {
            const string current = "    private void EndOperation(OperationGeneration generation)\n    {\n"
                + "        if (_operation.TryComplete(generation))\n        {\n            NotifyOperationStateChanged();\n        }\n    }";
            const string original = "    private void EndOperation(OperationGeneration generation)\n    {\n"
                + "        _operation.TryComplete(generation);\n        NotifyOperationStateChanged();\n    }";
            Assert.Equal(2, text.Split(current, StringSplitOptions.None).Length);
            return text.Replace(current, original, StringComparison.Ordinal);
        }
        return text;
    }

    internal sealed record ApprovedTextChange(string Path, string BeforeSha256, string AfterSha256, string BeforeText, string Provenance);
    private sealed record ApprovedTextHistory(int SchemaVersion, ApprovedTextChange[] Changes);
}
