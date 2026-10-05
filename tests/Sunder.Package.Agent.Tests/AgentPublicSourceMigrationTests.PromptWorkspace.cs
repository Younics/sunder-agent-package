using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Protocol;
using Sunder.Package.Agent.Services;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentPublicSourceMigrationTests
{
    private static async Task VerifyWorkspacePromptAsync(Assembly assembly, SunderRpcContractDescriptor descriptor)
    {
        var root = Directory.CreateTempSubdirectory("sunder-source-workspace-docs-").FullName;
        try
        {
            var work = Directory.CreateDirectory(Path.Combine(root, "work")).FullName;
            var first = Path.Combine(work, "first.md");
            var outside = Path.Combine(root, "explicit-guide.MDX");
            var unsupported = Path.Combine(work, "ignored.cs");
            var empty = Path.Combine(work, "empty.txt");
            File.WriteAllText(first, "First document 漢字");
            File.WriteAllText(outside, "Explicit external reference");
            File.WriteAllText(unsupported, "Unsupported file canary");
            File.WriteAllText(empty, string.Empty);
            File.WriteAllText(Path.Combine(work, "AGENTS.md"), "Unlisted instruction canary");
            var now = DateTimeOffset.UtcNow;
            var workspace = new AgentWorkspaceRecord("workspace.docs", "Workspace", null, now, now,
                [new("path.one", "workspace.docs", work, true, 0, now, now)],
                [Document("first", first, 10), Document("external", outside, 0), Document("duplicate", Path.Combine(work, ".", "first.md"), 20),
                    Document("unsupported", unsupported, 30), Document("empty", empty, 40), Document("missing", Path.Combine(work, "missing.md"), 50)]);
            var service = new WorkspaceDocumentationContextService();
            await using var fixture = await CreatePromptFixture(assembly, descriptor, service);
            var description = await InvokeGeneratedClient(fixture.Client, "DescribeAsync", EmptyRequest());
            Assert.Equal(service.ContributorId, description.GetProperty("contributorId").GetString());
            Assert.False(description.TryGetProperty("acknowledgmentSinkId", out _));
            var request = CreatePromptRequest() with { Workspace = workspace };
            var expected = RunJson(await service.ContributeContextAsync(request.DomainRequestWithoutTarget()));
            var actual = await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(request));
            Assert.True(JsonElement.DeepEquals(expected, actual));
            var block = Assert.Single(actual.GetProperty("blocks").EnumerateArray());
            Assert.Equal("reference", block.GetProperty("usage").GetString());
            Assert.Equal("reference", block.GetProperty("authority").GetString());
            Assert.Equal("tool", block.GetProperty("provenance").GetString());
            Assert.Equal("untrusted", block.GetProperty("trust").GetString());
            Assert.False(block.TryGetProperty("scope", out _));
            Assert.False(block.TryGetProperty("hostIdentity", out _));
            var content = block.GetProperty("content").GetString()!;
            Assert.True(content.IndexOf("Explicit external reference", StringComparison.Ordinal) < content.IndexOf("First document 漢字", StringComparison.Ordinal));
            Assert.Equal(2, content.Split("First document 漢字", StringSplitOptions.None).Length);
            Assert.DoesNotContain("Unsupported file canary", content, StringComparison.Ordinal);
            Assert.DoesNotContain("Unlisted instruction canary", content, StringComparison.Ordinal);

            foreach (var absent in new AgentWorkspaceRecord?[] { null, workspace with { Documents = [] }, workspace with { Documents = [Document("empty", empty, 0)] } })
                Assert.Equal(JsonValueKind.Null, (await InvokeGeneratedClient(fixture.Client, "ContributeAsync", RunJson(request with { Workspace = absent }))).ValueKind);
            var receipt = new AgentPromptContextReceipt(request.Session.SessionId, request.Run.RunId, request.TranscriptEpoch, []);
            Assert.Equal("{}", (await InvokeGeneratedClient(fixture.Client, "AcknowledgeAsync", RunJson(receipt))).GetRawText());

            var documents = new List<AgentWorkspaceDocumentRecord>();
            for (var index = 0; index < 7; index++)
            {
                var path = Path.Combine(work, $"large-{index}.txt");
                File.WriteAllText(path, new string((char)('a' + index), 12000) + "EXCLUDED-SUFFIX");
                documents.Add(Document("large-" + index, path, index));
            }
            var largeInput = request with { Workspace = workspace with { Documents = documents } };
            var largeRequest = RunJson(largeInput);
            var expectedLarge = RunJson(await service.ContributeContextAsync(largeInput.DomainRequestWithoutTarget()));
            var large = await InvokeGeneratedClient(fixture.Client, "ContributeAsync", largeRequest);
            Assert.True(JsonElement.DeepEquals(expectedLarge, large));
            var bounded = large.GetProperty("blocks")[0].GetProperty("content").GetString()!;
            Assert.DoesNotContain("EXCLUDED-SUFFIX", bounded, StringComparison.Ordinal);
            Assert.Equal(6, bounded.Split("[truncated]", StringSplitOptions.None).Length);
            Assert.DoesNotContain("large-5.txt", bounded, StringComparison.Ordinal);
            Assert.DoesNotContain("large-6.txt", bounded, StringComparison.Ordinal);
            Assert.InRange(bounded.Length, 60000, 65000);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InvokeGeneratedClient(fixture.Client, "ContributeAsync", largeRequest, canceled.Token));

            AgentWorkspaceDocumentRecord Document(string id, string path, int order) => new(id, "workspace.docs", path, order, now, now);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
