using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    // Owning identity guard78: d4d790a823262dac70c3c00dab94b90c470f76350c52633691773f671524fe38.
    // Preserve all preceding before-images, including the finite-constraint layer.
    private static string BeforeApprovedIdentityConstraints(string relative, string current)
    {
        current = BeforeApprovedChildDeadline(relative, current);
        var hashes = relative switch
        {
            "src/Sunder.Package.Agent/Models/Execution/AgentWorkspaceEditorContext.cs" =>
                (Before: "13c23b0f7839bfe7c2a5cabe9aaeb1819e14b36fe9a3adb326ddfcca4a902317", After: "1a2b630fc725957943d46739f5002f57d83d8a4118e31f3509192998fb873b49"),
            "src/Sunder.Package.Agent/Runtime/AgentChildRunRequest.cs" =>
                (Before: "b2c18437de163d10c54cca0f5c25b221c455329ef3cb9b31aacb0a0ceb487afd", After: "3f1faea0c4e4a102bb8c8706e51379e1db83be23448dbf6d6241b48cb61be50b"),
            "src/Sunder.Package.Agent/Models/Extensions/AgentServiceDescriptors.cs" =>
                (Before: "9716f83530c9d2fcbf73a22b1fc450c3d530da58de0ff9e48d181d94ffa012c0", After: "79b9cf953248c62817bc8b30c66f390629d1bcb6236a5dc15883f6c31f67e08b"),
            _ => (Before: (string?)null, After: (string?)null),
        };
        if (hashes.Before is null) return current;
        AssertProjectHash(hashes.After!, current, relative + " (approved required identity current source)");
        const string annotation = "[property: Sunder.Sdk.Services.SunderValueConstraints(MinLength = 1)] ";
        Assert.Equal(2, current.Split(annotation, StringSplitOptions.None).Length);
        var before = current.Replace(annotation, "", StringComparison.Ordinal);
        AssertProjectHash(hashes.Before, before, relative + " (authenticated identity before-image)");
        return before;
    }
}
