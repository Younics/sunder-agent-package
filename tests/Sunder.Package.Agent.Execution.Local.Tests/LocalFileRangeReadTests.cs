using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalFileRangeReadTests : IDisposable
{
    private readonly string _root = Path.Combine(
        OperatingSystem.IsMacOS() ? "/private" + Path.GetTempPath().TrimEnd(Path.DirectorySeparatorChar) : Path.GetTempPath(),
        "sunder-local-range-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ReadFileAsync_WithRange_ReturnsOnlyRequestedLines()
    {
        Directory.CreateDirectory(_root);
        var path = Path.Combine(_root, "large.txt");
        await File.WriteAllLinesAsync(path, Enumerable.Range(1, 5000).Select(index => $"line-{index}"));
        var config = new LocalExecutionRuntimeConfig([_root], _root);

        var result = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest("large.txt", Offset: 2500, Limit: 2),
            allowOutsideConfiguredScope: false,
            CancellationToken.None);

        Assert.Equal("line-2500\nline-2501", result.Content);
        Assert.True(result.WasTruncated);
    }

    [Fact]
    public async Task ReadFileAsync_FullReadAndDirectoryListing_AreBounded()
    {
        Directory.CreateDirectory(_root);
        await File.WriteAllBytesAsync(
            Path.Combine(_root, "oversized.txt"),
            Enumerable.Repeat((byte)'x', AgentPayloadLimits.MaxLocalFullFileReadBytes + 1).ToArray());
        var directory = Path.Combine(_root, "many");
        Directory.CreateDirectory(directory);
        for (var index = 0; index <= AgentPayloadLimits.MaxLocalDirectoryEntries; index++)
        {
            File.Create(Path.Combine(directory, $"{index:D5}.txt")).Dispose();
        }

        var config = new LocalExecutionRuntimeConfig([_root], _root);
        var file = await LocalFileSystemExecutor.ReadFileAsync(config, new AgentFileReadRequest("oversized.txt"), false, CancellationToken.None);
        var listing = await LocalFileSystemExecutor.ReadFileAsync(config, new AgentFileReadRequest("many"), false, CancellationToken.None);

        Assert.True(file.IsError);
        Assert.Equal(AgentFileReadErrorCodes.TooLarge, file.ErrorCode);
        Assert.True(listing.WasTruncated);
        Assert.Equal(AgentPayloadLimits.MaxLocalDirectoryEntries, listing.Content.Split(Environment.NewLine).Length);
    }

    [Fact]
    public async Task MutationGates_AreEvictedAfterUse()
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);

        for (var index = 0; index < 100; index++)
        {
            var result = await LocalFileSystemExecutor.WriteFileAsync(
                config,
                new AgentFileWriteRequest($"file-{index}.txt", "content"),
                false,
                CancellationToken.None);
            Assert.False(result.IsError, result.Summary);
        }

        Assert.Equal(0, LocalFileSystemExecutor.MutationGateCount);
    }

    [Fact]
    public async Task RangedReadAndSearch_EnforceByteAndLineBudgets()
    {
        Directory.CreateDirectory(_root);
        var config = new LocalExecutionRuntimeConfig([_root], _root);
        await File.WriteAllTextAsync(
            Path.Combine(_root, "long-line.txt"),
            "needle" + new string('x', HostFileSystemLimits.MaxLineCharacters));
        await using (var oversized = new FileStream(
                         Path.Combine(_root, "oversized-range.txt"),
                         FileMode.CreateNew,
                         FileAccess.Write,
                         FileShare.None))
        {
            oversized.SetLength(HostFileSystemLimits.MaxRangedReadBytes + 1);
        }

        var longLine = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest("long-line.txt", Offset: 1, Limit: 1),
            false,
            CancellationToken.None);
        var oversizedRange = await LocalFileSystemExecutor.ReadFileAsync(
            config,
            new AgentFileReadRequest("oversized-range.txt", Offset: 1, Limit: 1),
            false,
            CancellationToken.None);
        var search = await LocalSecureFileSearch.ExecuteAsync(
            config,
            new AgentFileSearchRequest("long-line.txt", AgentFileSearchKind.Grep, "needle"),
            false,
            approvedResourceReferences: null,
            CancellationToken.None);

        Assert.Equal(AgentFileReadErrorCodes.TooLarge, longLine.ErrorCode);
        Assert.Equal(AgentFileReadErrorCodes.TooLarge, oversizedRange.ErrorCode);
        Assert.Empty(search.Matches);
        Assert.True(search.WasTruncated);
    }

    [Fact]
    public void TraversalBudget_EnforcesDepthEntryNameAndCancellationBounds()
    {
        Assert.Throws<LocalSecurePathException>(() =>
            new HostTraversalBudget(CancellationToken.None)
                .Visit(HostFileSystemLimits.MaxTraversalDepth + 1, "entry"));

        var entries = new HostTraversalBudget(CancellationToken.None);
        for (var index = 0; index < HostFileSystemLimits.MaxTraversalEntries; index++)
        {
            entries.Visit(0, string.Empty);
        }
        Assert.Throws<LocalSecurePathException>(() => entries.Visit(0, string.Empty));

        var names = new HostTraversalBudget(CancellationToken.None);
        var largeName = new string('x', 1024 * 1024);
        for (var index = 0; index < 16; index++)
        {
            names.Visit(0, largeName);
        }
        Assert.Throws<LocalSecurePathException>(() => names.Visit(0, "x"));

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            new HostTraversalBudget(cancellation.Token).Visit(0, "entry"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
