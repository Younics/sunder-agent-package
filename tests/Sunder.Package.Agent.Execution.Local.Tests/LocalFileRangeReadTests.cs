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

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
