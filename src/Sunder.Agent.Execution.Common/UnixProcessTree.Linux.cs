namespace Sunder.Agent.Execution.Common;

internal static partial class UnixProcessTree
{
    private static bool TryGetLinuxChildProcessIds(int processId, out IReadOnlyList<int> children)
    {
        try
        {
            children = File.ReadAllText($"/proc/{processId}/task/{processId}/children")
                .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
                .ToArray();
            return true;
        }
        catch (FileNotFoundException) { children = []; return true; }
        catch (DirectoryNotFoundException) { children = []; return true; }
    }
}
