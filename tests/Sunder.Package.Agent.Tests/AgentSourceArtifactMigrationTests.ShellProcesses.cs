using System.Diagnostics;
using System.Globalization;
using Sunder.Package.Agent.Contracts.Models;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task VerifyShellProcesses(string root,
        Func<string, object, AgentToolExecutionContext?, CancellationToken, Task<AgentToolResult>> execute,
        Func<Task> retireAndReplace, IAsyncDisposable lifetime)
    {
        var timeout = await execute("shell", new { command = "exec sleep 30", timeoutSeconds = 1 }, null, default);
        Assert.True(timeout.IsError); Assert.Equal(AgentToolResultErrorCodes.ShellTimeout, timeout.ErrorCode); Assert.True(timeout.RequiresPromptContextRefresh);
        foreach (var mode in new[] { "cancel", "retire", "shutdown" })
        {
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var marker = Path.Combine(root, "started-" + mode);
            var pending = execute("shell", new { command = $"printf '%s' \"$$\" > '{marker}'; exec sleep 30", timeoutSeconds = 8 }, null, cancellation.Token);
            using var process = await WaitForShellProcess(marker);
            if (mode == "cancel")
            {
                cancellation.Cancel();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            }
            else if (mode == "retire")
            {
                var retirement = retireAndReplace();
                Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, (await pending).ErrorCode);
                await retirement.WaitAsync(TimeSpan.FromSeconds(10));
            }
            else
            {
                var shutdown = lifetime.DisposeAsync().AsTask();
                Assert.Equal(AgentToolResultErrorCodes.PackageUnavailable, (await pending).ErrorCode);
                await shutdown.WaitAsync(TimeSpan.FromSeconds(10));
            }
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(process.HasExited);
        }
    }

    private static async Task<Process> WaitForShellProcess(string marker)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!timeout.IsCancellationRequested)
        {
            if (File.Exists(marker) && int.TryParse(await File.ReadAllTextAsync(marker, timeout.Token), NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                return Process.GetProcessById(pid);
            await Task.Delay(10, timeout.Token);
        }
        throw new TimeoutException("The test-owned shell did not publish its process identity.");
    }
}
