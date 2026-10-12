using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Owner handoff92: 3684d5531066c84dc9fac2a4bb6cd8771d511dec8425617254a0bfd49557758e.
    // Reverse only the reviewed business-boundary caller-token restoration.
    private static string BeforeApprovedSubagentCancellation(string relative, string current)
    {
        current = BeforeOwnedPublicServices(relative, current);
        if (relative != "src/Sunder.Package.Agent.Subagents/Services/SubagentCatalogSelection.cs") return current;
        AssertProjectHash("1eaabe5ea0cf1cad7e579276f7f2d5c64aee7d03b4bd813d54d739deb57ac4d6", current, relative);
        const string restoration = "        catch (OperationCanceledException error) when (token.IsCancellationRequested)\n"
            + "        {\n"
            + "            if (error.CancellationToken == token) throw;\n"
            + "            // The generated client receives our linked token. Keep its SDK cause\n"
            + "            // directly visible while restoring the business caller's identity.\n"
            + "            throw new OperationCanceledException(error.Message, error.InnerException, token);\n"
            + "        }\n";
        Assert.Equal(2, current.Split(restoration, StringSplitOptions.None).Length);
        var before = current.Replace(restoration, "", StringComparison.Ordinal);
        AssertProjectHash("2b001b2f202c5520fef3f18b7044535b387fb8c3fa5dae8b4ed070d854703ba5", before, relative);
        return before;
    }
}
