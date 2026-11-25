namespace Sunder.Package.Agent.Tests;

internal static class FrozenMemoryProbeProgram
{
    internal const string Argument = "--frozen-memory-transport-probe";
    internal const string Completion = "Frozen Memory Semantic transport probe completed.";

    private static async Task<int> Main(string[] args)
    {
        if (args is not [Argument])
        {
            Console.Error.WriteLine($"Expected {Argument}; run ordinary tests with dotnet test.");
            return 1;
        }

        try
        {
            await AgentCatalogSourceMigrationTests.VerifyFrozenWorkerProbeAsync(
                Directory.GetCurrentDirectory(), "Memory.Semantic", fixture: null);
            Console.WriteLine(Completion);
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
    }
}
