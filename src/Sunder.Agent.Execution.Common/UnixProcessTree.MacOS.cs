using System.Runtime.InteropServices;

namespace Sunder.Agent.Execution.Common;

internal static partial class UnixProcessTree
{
    private static bool TryGetMacChildProcessIds(int processId, out IReadOnlyList<int> children)
    {
        var count = ListMacChildProcesses(processId, null, 0);
        if (count < 0) { children = []; return false; }
        if (count == 0) { children = []; return true; }

        var buffer = new int[Math.Min(count + 16, MaximumProcessTreeSize)];
        var result = ListMacChildProcesses(processId, buffer, checked(buffer.Length * sizeof(int)));
        if (result < 0) { children = []; return false; }
        var resultCount = result <= buffer.Length ? result : result / sizeof(int);
        if (resultCount > buffer.Length) { children = []; return false; }
        children = buffer[..resultCount];
        return true;
    }

    [DllImport("/usr/lib/libproc.dylib", EntryPoint = "proc_listchildpids", SetLastError = true)]
    private static extern int ListMacChildProcesses(int parentProcessId, [Out] int[]? buffer, int bufferSize);
}
