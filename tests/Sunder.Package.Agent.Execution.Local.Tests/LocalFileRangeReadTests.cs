using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Execution.Local.Tests;

public sealed class LocalFileRangeReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sunder-local-range-tests", Guid.NewGuid().ToString("N"));

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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
