using System.Text.Json;
using System.Xml.Linq;
using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal const string PackedProtocolConsumerProject = "fixtures/PackedProtocolConsumer/PackedProtocolConsumer.csproj";
    internal static IReadOnlyList<RetirementCapture> PackedProtocolConsumerCaptures => PackedProtocolHistory.Bundle.Entries;

    private static void AssertPackedProtocolReplacement(string root)
    {
        AssertCanonicalProjectTopology(root);
        Assert.DoesNotContain(XDocument.Load(Path.Combine(root, "Sunder.AgentPackage.slnx")).Descendants("Project"),
            project => ((string?)project.Attribute("Path"))?.Replace('\\', '/') == PackedProtocolConsumerProject);
        foreach (var replacement in PackedProtocolHistory.Bundle.Replacements)
            Assert.True(File.Exists(Path.Combine(root, replacement)), $"Packed Protocol consumer replacement is missing: {replacement}");

        // The obsolete executable checked the removed process-search ABI through a
        // shared NuGet assembly. Its current equivalent checks the canonical owner's
        // owned descriptor, together with the required generated-role/archive tests.
        using var owned = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "src/Sunder.Package.Agent/sunder-contracts.owned.json")));
        var execution = Assert.Single(owned.RootElement.GetProperty("contracts").EnumerateObject(),
            contract => contract.Value.GetProperty("contractId").GetString() == "sunder.agent.execution.target");
        using var descriptor = JsonDocument.Parse(execution.Value.GetProperty("current").GetProperty("canonicalDescriptor").GetString()!);
        Assert.DoesNotContain(descriptor.RootElement.GetProperty("services").EnumerateObject()
            .SelectMany(service => service.Value.GetProperty("methods").EnumerateObject()), method => method.Name == "legacy-file-search");
        Assert.False(descriptor.RootElement.GetProperty("$defs").TryGetProperty("AgentFileSearchProcessRequest", out _));
        Assert.False(descriptor.RootElement.GetProperty("$defs").TryGetProperty("AgentFileSearchProcessResult", out _));
        Assert.Contains("AgentSourceArtifactMigrationTests.CompleteOwnerArtifact_DerivesConsumerAndProviderLocksFromFreshSourcePayload",
            PackedProtocolHistory.Bundle.RequiredTests);
    }

    private static class PackedProtocolHistory
    {
        internal static readonly PackedProtocolBundle Bundle = Read();

        private static PackedProtocolBundle Read()
        {
            var root = AgentPackageRepositoryInventory.RepositoryRoot.FullName;
            var text = ReadNormalizedProjectText(Path.Combine(root, PreCanonicalProjectsDirectory, "packed-protocol-consumer-history.json"));
            AssertProjectHash("93302cba48845d76570ea7b6f8fd83bd2fbb33e6bf88a227ecaed6bbb9da1e1d", text, "packed-protocol-consumer-history.json");
            var bundle = JsonSerializer.Deserialize<PackedProtocolBundle>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            Assert.Equal(1, bundle.SchemaVersion);
            Assert.Equal(new[]
            {
                PackedProtocolConsumerProject,
                "fixtures/PackedProtocolConsumer/Program.cs",
                "scripts/compatibility/verify-packed-protocol.sh",
            }, bundle.Entries.Select(entry => entry.Path));
            Assert.Equal(new[] { "retired-fixture-project", "retired-fixture-source", "retired-fixture-tool" }, bundle.Entries.Select(entry => entry.Kind));
            using var baseline = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(root, "compatibility/agent-rpc-migration-baseline.json")));
            var originalProjects = baseline.RootElement.GetProperty("buildTopology").GetProperty("projects");
            Assert.Equal(57, originalProjects.GetArrayLength());
            Assert.DoesNotContain(originalProjects.EnumerateArray(), project => project.GetProperty("path").GetString() == PackedProtocolConsumerProject);
            foreach (var entry in bundle.Entries)
            {
                AssertProjectHash(entry.CaptureSha256, entry.Text, entry.Path + " (authenticated fixture/tool capture)");
                Assert.Equal(entry.CaptureSha256, entry.RawSha256); // All three actual captures were already UTF-8/LF.
                Assert.StartsWith("git-index-and-HEAD:", entry.Provenance, StringComparison.Ordinal);
                Assert.False(entry.InFrozenProjectInventory);
                Assert.False(entry.InOriginalSolution);
                Assert.Null(entry.OriginalSha256); // There is no invented 58th frozen project.
            }
            return bundle;
        }
    }

    private sealed record PackedProtocolBundle(int SchemaVersion, string Scope, RetirementCapture[] Entries, string[] Replacements, string[] RequiredTests);
}
