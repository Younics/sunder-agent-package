using System.Runtime.InteropServices;

namespace Sunder.Agent.Execution.Common;

// Best-effort native cleanup used before the runner's existing Process.Kill fallback.
// Output capture, cancellation and pipe draining remain owned by the runner.
internal static partial class UnixProcessTree
{
    private const int MaximumProcessTreeSize = 4096;

    internal static bool TryKill(int rootProcessId)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return false;
        try
        {
            var processIds = new List<int> { rootProcessId };
            var knownProcessIds = new HashSet<int> { rootProcessId };
            var stablePasses = 0;
            for (var pass = 0; pass < 16 && stablePasses < 2; pass++)
            {
                foreach (var processId in processIds)
                    _ = KillUnixProcess(processId, OperatingSystem.IsMacOS() ? 17 : 19);

                Thread.Sleep(TimeSpan.FromMilliseconds(5));
                var added = false;
                for (var index = 0; index < processIds.Count; index++)
                {
                    if (!TryGetChildProcessIds(processIds[index], out var children)) return false;
                    foreach (var child in children)
                    {
                        if (child <= 0 || !knownProcessIds.Add(child)) continue;
                        if (processIds.Count >= MaximumProcessTreeSize) return false;
                        processIds.Add(child);
                        added = true;
                    }
                }
                stablePasses = added ? 0 : stablePasses + 1;
            }
            for (var index = processIds.Count - 1; index >= 0; index--)
                _ = KillUnixProcess(processIds[index], 9);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException
                                   or OverflowException or DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool TryGetChildProcessIds(int processId, out IReadOnlyList<int> children)
        => OperatingSystem.IsMacOS()
            ? TryGetMacChildProcessIds(processId, out children)
            : TryGetLinuxChildProcessIds(processId, out children);

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int KillUnixProcess(int processId, int signal);
}
