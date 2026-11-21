using System;
using System.Threading.Tasks;
using Sunder.Package.Agent.Tests;

internal static class WorkspaceProbeEntry
{
    private static async Task<int> Main()
    {
        try
        {
            var tests = new WorkspacePersistenceTests();
            await tests.AgentLocalStore_AsyncWorkspaceReads_UseIndependentConcurrentOperations();
            Console.WriteLine("Exact workspace read fixture completed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
