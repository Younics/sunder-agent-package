using System.Text;
using System.Text.Json;
using Sunder.Package.Agent.Contracts.Models;
using Sunder.Package.Agent.Tests;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed partial class FilesToolSourceRefactorTests
{
    [Theory]
    [InlineData("{\"path\":\"file.txt\",\"offset\":1,\"limit\":10}")]
    [InlineData("{\"path\":\"file.txt\",\"oldString\":\"same\",\"newString\":\"same\"}")]
    [InlineData("{\"path\":\"file.txt\",\"oldString\":\"old\",\"newString\":null}")]
    public async Task EditArgumentValidation_IsPureAndRejectsReadShapedOrUnchangedEdits(string json)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["file.txt"] = "old" });
        var (source, _, _) = CreateSource(target);
        var result = await source.ValidateArgumentsAsync(new("edit", json));
        Assert.True(result?.IsError);
        Assert.Equal("files-arguments-invalid", result?.ErrorCode);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Equal("old", target.Files["file.txt"]);
    }

    [Fact]
    public async Task EditArgumentValidation_AcceptsOptionalNullAndEditSummaryUsesChangedLines()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        { ["file.txt"] = new string('x', 16000) + "\nold\n" });
        var (source, _, context) = CreateSource(target);
        var request = new AgentToolRequest("edit", "{\"path\":\"file.txt\",\"oldString\":\"old\",\"newString\":\"new\",\"replaceAll\":null}");
        Assert.Null(await source.ValidateArgumentsAsync(request));
        var result = await source.ExecuteAsync(context, request);
        Assert.False(result.IsError, result.Content);
        Assert.Contains("1 replacement(s), +1/-1 lines", result.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain("16000", result.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("\n", "1: ")]
    [InlineData("\n\n", "1: \n2: ")]
    [InlineData("text\n\n", "1: text\n2: ")]
    public async Task NativeRead_PreservesEmptySelectedLines(string content, string expected)
    {
        using var scope = RegressionTestPackageScope.Create();
        var root = Path.Combine(scope.RootPath, "workspace");
        var (source, context) = await CreateLocalSourceAsync(scope, root);
        await File.WriteAllTextAsync(Path.Combine(root, "file.txt"), content);

        var result = await source.ExecuteAsync(context, new("read", "{\"path\":\"file.txt\"}"));

        Assert.False(result.IsError, result.Content);
        Assert.False(result.WasTruncated);
        Assert.Equal(expected.Replace("\n", Environment.NewLine, StringComparison.Ordinal), result.Content);
    }

    [Theory]
    [InlineData("", 2)]
    [InlineData("one\n", 2)]
    [InlineData("one\ntwo", 3)]
    public async Task LegacyRead_OutOfRangeIsNotAnEmptySuccessfulRead(string content, int offset)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["file.txt"] = content });
        var (source, _, context) = CreateSource(target);

        var result = await source.ExecuteAsync(context, new("read", JsonSerializer.Serialize(new { path = "file.txt", offset })));

        Assert.True(result.IsError);
        Assert.Equal(AgentFileReadErrorCodes.RangeOutsideFile, result.ErrorCode);
    }

    [Fact]
    public async Task LegacyRead_BoundsBytesAndLongLinesAndOffersAContinuation()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string>
        { ["file.txt"] = string.Join('\n', Enumerable.Range(1, 100).Select(index => index + new string('界', 3000))) });
        var (source, _, context) = CreateSource(target);
        var result = await source.ExecuteAsync(context, new("read", "{\"path\":\"file.txt\"}"));
        Assert.False(result.IsError, result.Content);
        Assert.True(result.WasTruncated);
        Assert.True(Encoding.UTF8.GetByteCount(result.Content!) <= 50 * 1024);
        Assert.Contains("line shortened", result.Content, StringComparison.Ordinal);
        Assert.Contains("Continue with offset=", result.Content, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GuardedWriteAndEdit_RejectStaleContentWithoutBlindRetry(bool edit)
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["file.txt"] = "old" })
        { BeforeMutationCall = 1, BeforeMutation = current => current.Files["file.txt"] = "concurrent" };
        var (source, _, context) = CreateSource(target);
        var args = edit ? "{\"path\":\"file.txt\",\"oldString\":\"old\",\"newString\":\"new\"}"
            : "{\"path\":\"file.txt\",\"content\":\"new\"}";
        var result = await source.ExecuteAsync(context, new(edit ? "edit" : "write", args));
        Assert.True(result.IsError);
        Assert.Equal("file-content-changed", result.ErrorCode);
        Assert.Equal(1, target.MutationCallCount);
        Assert.Equal("concurrent", target.Files["file.txt"]);
    }

    [Fact]
    public async Task AmbiguousEdit_PerformsNoMutations()
    {
        var target = new MemoryExecutionTarget(new Dictionary<string, string> { ["file.txt"] = "old\nold" });
        var (source, _, context) = CreateSource(target);
        var edit = await source.ExecuteAsync(context, new("edit", "{\"path\":\"file.txt\",\"oldString\":\"old\",\"newString\":\"new\"}"));
        Assert.True(edit.IsError);
        Assert.Contains("matches 2", edit.Content, StringComparison.Ordinal);
        Assert.Equal(0, target.MutationCallCount);
        Assert.Equal("old\nold", target.Files["file.txt"]);
    }
}
