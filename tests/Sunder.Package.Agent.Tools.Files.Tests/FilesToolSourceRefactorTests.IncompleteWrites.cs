using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class FilesToolSourceRefactorTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ApplyPatch_IncompleteWrite_IsNeverReportedFullyRestored(bool reachesPlannedContent)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        })
        {
            IncompleteWriteCall = 2,
            IncompleteWriteReachesPlannedContent = reachesPlannedContent,
        };
        var (source, _, context) = CreateSource(target);
        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", TwoUpdates()));
        Assert.True(result.IsError);
        Assert.Equal("patch-partial-application", result.ErrorCode);
        Assert.Contains("Compensation restored 1 operation", result.Content, StringComparison.Ordinal);
        Assert.Contains("reported an incomplete mutation", result.Content, StringComparison.Ordinal);
        Assert.Contains("may remain partially modified", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Applied patch file changes were restored", result.Content, StringComparison.Ordinal);
        Assert.Equal(3, target.MutationCallCount);
        Assert.Equal("old first", target.Files["first.txt"]);
        Assert.Equal(reachesPlannedContent ? "new second" : "partially written content", target.Files["second.txt"]);
    }

    [Fact]
    public async Task ApplyPatch_MissingMutationReceipt_DoesNotAdoptAReResolvedFile()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        {
            ["first.txt"] = "old first",
            ["second.txt"] = "old second",
        })
        {
            FailMutationCall = 2,
            OmitMutationReceipts = true,
        };
        var (source, _, context) = CreateSource(target);
        var result = await source.ExecuteAsync(context, new AgentToolRequest("apply_patch", TwoUpdates()));
        Assert.True(result.IsError);
        Assert.Contains("automatic compensation was skipped", result.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Applied patch file changes were restored", result.Content, StringComparison.Ordinal);
        Assert.Equal(2, target.MutationCallCount);
        Assert.Equal("new first", target.Files["first.txt"]);
        Assert.Equal("old second", target.Files["second.txt"]);
    }

    private static string TwoUpdates() => JsonSerializer.Serialize(new
    {
        patchText = "*** Begin Patch\n*** Update File: first.txt\n@@\n-old first\n+new first\n*** Update File: second.txt\n@@\n-old second\n+new second\n*** End Patch",
    });
}
