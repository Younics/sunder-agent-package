extern alias LocalWorker;

using System.Text;
using LocalWorker::Sunder.Package.Agent.Execution.Local;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;
using ReadRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileReadRequest;
using WriteRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileWriteRequest;
using SearchRequest = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchRequest;
using SearchKind = LocalWorker::Sunder.Generated.Packages.Younics_Ai_Agent_Models.AgentFileSearchKind;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class LocalFileRangeReadTests
{
    [Fact]
    public async Task Search_UsesLiteralCaseAndCallerLimitAndReportsTheLimitingBound()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllTextAsync(Path.Combine(_root, "file.txt"), "[Needle]\n[needle]\nnot-needle");
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        var request = new SearchRequest { Path = "file.txt", Kind = SearchKind.Grep, Pattern = "[Needle]", Literal = true, CaseSensitive = true, Hidden = false };
        var exact = await LocalSecureFileSearch.ExecuteAsync(config, request, false, null, CancellationToken.None);
        var insensitive = await LocalSecureFileSearch.ExecuteAsync(config, request with { CaseSensitive = false, Limit = 1 }, false, null, CancellationToken.None);
        Assert.False(exact.IsError, exact.ErrorMessage);
        Assert.Single(exact.Matches);
        Assert.False(exact.WasTruncated);
        Assert.Single(insensitive.Matches);
        Assert.True(insensitive.WasTruncated);
        Assert.Equal("result-limit", insensitive.TruncationReason);
        var invalid = await LocalSecureFileSearch.ExecuteAsync(config, request with { Limit = 1001 }, false, null, CancellationToken.None);
        Assert.True(invalid.IsError);
        Assert.Empty(invalid.Matches);
    }

    [Theory]
    [InlineData("**/*.txt", false, 1)]
    [InlineData("**/*.txt", true, 3)]
    [InlineData(".config/*.txt", false, 1)]
    public async Task Glob_ExcludesHiddenSegmentsUnlessExplicitOrRequested(string pattern, bool hidden, int count)
    {
        Directory.CreateDirectory(Path.Combine(_root, ".config"));
        await File.WriteAllTextAsync(Path.Combine(_root, "visible.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_root, ".hidden.txt"), "text");
        await File.WriteAllTextAsync(Path.Combine(_root, ".config", "settings.txt"), "text");
        var result = await LocalSecureFileSearch.ExecuteAsync(new([_root], _root),
            new SearchRequest { Path = ".", Kind = SearchKind.Glob, Pattern = pattern, Hidden = hidden, Literal = false }, false, null, CancellationToken.None);
        Assert.False(result.IsError, result.ErrorMessage);
        Assert.Equal(count, result.Matches.Count);
        Assert.False(result.WasTruncated);
    }

    [Fact]
    public async Task FullMutationSnapshot_PreservesBomAndRejectsInvalidUtf8()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "bom.txt");
        await File.WriteAllTextAsync(path, "one\r\ntwo", new UTF8Encoding(true));
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        var snapshot = await LocalFileSystemExecutor.ReadFileAsync(config, new ReadRequest { Path = "bom.txt" }, false, CancellationToken.None);
        Assert.Equal("\uFEFFone\r\ntwo", snapshot.Content);
        var written = await LocalFileSystemExecutor.WriteFileAsync(config, new WriteRequest
        { Path = "bom.txt", Content = "changed\r\ntwo", Overwrite = true, ExpectedContentHash = FileOperation.ComputeContentHash(snapshot.Content) }, false, CancellationToken.None);
        Assert.False(written.IsError, written.Summary);
        Assert.Equal(Encoding.UTF8.GetBytes("\uFEFFchanged\r\ntwo"), await File.ReadAllBytesAsync(path));

        var invalidBytes = new byte[] { 0xC3, 0x28 };
        await File.WriteAllBytesAsync(path, invalidBytes);
        var invalid = await LocalFileSystemExecutor.ReadFileAsync(config, new ReadRequest { Path = "bom.txt" }, false, CancellationToken.None);
        Assert.Equal(AgentFileReadErrorCodes.BinaryFile, invalid.ErrorCode);
        Assert.Equal(invalidBytes, await File.ReadAllBytesAsync(path));
    }

    [Fact]
    public async Task DirectoryRead_PagesSortedEntriesWithoutRepeatingTheFirstPage()
    {
        Directory.CreateDirectory(_root);
        foreach (var name in new[] { "c.txt", "a.txt", "b.txt" }) await File.WriteAllTextAsync(Path.Combine(_root, name), "");
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        var first = await LocalFileSystemExecutor.ReadFileAsync(config, new ReadRequest { Path = ".", Offset = 1, Limit = 2 }, false, CancellationToken.None);
        var next = await LocalFileSystemExecutor.ReadFileAsync(config, new ReadRequest { Path = ".", Offset = 3, Limit = 2 }, false, CancellationToken.None);
        Assert.Equal("a.txt" + Environment.NewLine + "b.txt", first.Content);
        Assert.True(first.WasTruncated);
        Assert.Equal("c.txt", next.Content);
        Assert.False(next.WasTruncated);
        Assert.Equal(3, next.TotalLines);
        var outside = await LocalFileSystemExecutor.ReadFileAsync(config, new ReadRequest { Path = ".", Offset = 4, Limit = 2 }, false, CancellationToken.None);
        Assert.Equal(AgentFileReadErrorCodes.RangeOutsideFile, outside.ErrorCode);
    }
}
