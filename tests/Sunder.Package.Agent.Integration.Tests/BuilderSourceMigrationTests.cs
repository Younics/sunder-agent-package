using System.Diagnostics;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderSourceMigrationTests
{
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

    internal static Task<string> Run(string directory, params string[] arguments)
        => Run(directory, TimeSpan.FromMinutes(3), arguments);

    // SDK commands must retain the build host even when VSTest uses a newer CLR.
    internal static string DotnetBuildHost => Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";

    // Temporary probe projects use Debug even under Release CI. Authored package
    // builds and their retained role snapshots use CanonicalPackageRoles.Configuration.
    internal const string FixtureConfiguration = "Debug";

    internal static string BuildTaskAssembly
    {
        get
        {
            var path = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "sunder-build-task-path.txt")).Trim();
            Assert.True(File.Exists(path), "Restore the project's declared Sunder.Package.Build dependency: " + path);
            return path;
        }
    }

    internal static async Task<string> Run(string directory, TimeSpan timeLimit, params string[] arguments)
    {
        var start = new ProcessStartInfo(DotnetBuildHost) { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(arguments[0]);
        // Newer SDK CLIs can infer --configuration from the inherited environment
        // independently of forwarded MSBuild properties. Pin the CLI option too.
        if (arguments[0] is "build" or "run")
        {
            start.ArgumentList.Add("--configuration");
            start.ArgumentList.Add(FixtureConfiguration);
        }
        if (arguments[0] != "exec")
        {
            start.ArgumentList.Add("-p:Configuration=" + FixtureConfiguration);
            start.ArgumentList.Add("-v:minimal");
        }
        foreach (var argument in arguments.Skip(1)) start.ArgumentList.Add(argument);
        if (arguments[0] != "restore") DenyNetwork(start);
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
        var diagnostics = await output + Environment.NewLine + await error;
        Assert.True(process.ExitCode == 0, diagnostics);
        return diagnostics;
    }

    internal static void DenyNetwork(ProcessStartInfo start)
    {
        foreach (var key in new[] { "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "http_proxy", "https_proxy", "all_proxy" })
            start.Environment[key] = "http://127.0.0.1:1";
        start.Environment["NO_PROXY"] = start.Environment["no_proxy"] = "";
    }

    internal static string RequireProbeOutput(string directory, string assemblyName, string diagnostics)
    {
        var path = Path.Combine(directory, "bin", FixtureConfiguration, "net10.0", assemblyName + ".dll");
        Assert.True(File.Exists(path), $"Probe build host '{DotnetBuildHost}' did not emit '{path}'.{Environment.NewLine}"
            + diagnostics + Environment.NewLine + "Observed assemblies: "
            + string.Join(", ", Directory.EnumerateFiles(directory, "*.dll", SearchOption.AllDirectories).Take(32)));
        return path;
    }
}

internal sealed class BuilderSourceMigrationFactAttribute : FactAttribute
{
    public BuilderSourceMigrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SUNDER_REQUIRE_BUILDER_SOURCE_MIGRATION") == "1") return;
        if (!File.Exists(Environment.GetEnvironmentVariable("SUNDER_AGENT_SOURCE_PLATFORM_CATALOG")))
            Skip = "Requires the public catalog selected by the Agent locks.";
    }
}
