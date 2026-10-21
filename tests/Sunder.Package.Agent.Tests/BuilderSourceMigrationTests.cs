using System.Diagnostics;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderSourceMigrationTests
{
    // Current source behavior is qualified independently of protected history.
    private static readonly string[] WorkerSourcePaths =
    [
        "src/Sunder.Package.Agent.Builder/Runtime/BuilderRuntimeService.cs",
        "src/Sunder.Package.Agent.Builder/Runtime/BuilderContent.cs",
    ];
    private const string AppSourcePath = "src/Sunder.Package.Agent.Builder/App/BuilderBackendGateway.cs";
    internal static readonly string[] SourcePaths = [.. WorkerSourcePaths, AppSourcePath];

    [BuilderSourceMigrationFact]
    [Trait("Category", "SourceMigration")]
    public async Task BuilderSource_LocalPrivateBackendUsesGeneratedRolesAndPreservesReviewedInputs()
    {
        var root = CanonicalPackageRoles.Source("Builder");
        var baseline = CanonicalPackageRoles.ReadOwnedBaseline(root);
        var selected = File.ReadAllBytes(Path.Combine(root, "sunder-contracts.lock.json"));
        try
        {
            await AgentSourceArtifactMigrationTests.VerifyBuilderRoles(root);
        }
        finally
        {
            Assert.Equal(baseline, CanonicalPackageRoles.ReadOwnedBaseline(root));
            Assert.Equal(selected, File.ReadAllBytes(Path.Combine(root, "sunder-contracts.lock.json")));
        }
    }

    internal static string FindRepository()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "Sunder.AgentPackage.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Agent repository root was not found.");
    }

    internal static Task Run(string directory, params string[] arguments)
        => Run(directory, TimeSpan.FromMinutes(3), arguments);

    // SDK commands must retain the build host even when VSTest uses a newer CLR.
    internal static string DotnetBuildHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    internal static async Task Run(string directory, TimeSpan timeLimit, params string[] arguments)
    {
        var start = new ProcessStartInfo(DotnetBuildHost) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.ArgumentList.Add("-v:minimal");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet did not start.");
        using var timeout = new CancellationTokenSource(timeLimit);
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException exception) when (timeout.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
            throw new TimeoutException($"dotnet {string.Join(' ', arguments)} exceeded its {timeLimit} subprocess limit in '{directory}'.{Environment.NewLine}"
                + await output + Environment.NewLine + await error, exception);
        }
        catch { if (!process.HasExited) process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        Assert.True(process.ExitCode == 0, await output + Environment.NewLine + await error);
    }
}

internal sealed class BuilderSourceMigrationFactAttribute : FactAttribute
{
    public BuilderSourceMigrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SUNDER_REQUIRE_BUILDER_SOURCE_MIGRATION") == "1") return;
        var root = BuilderSourceMigrationTests.FindRepository();
        if (!File.Exists(Path.GetFullPath(Path.Combine(root, "..", "sunder-core", "Sunder.Core.slnx"))))
            Skip = "Requires sibling Core source/tooling. Set SUNDER_REQUIRE_BUILDER_SOURCE_MIGRATION=1 to require this source-migration lane.";
    }
}
