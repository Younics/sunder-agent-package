using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using Sunder.Package.Agent.Protocol;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class BuilderSourceMigrationTests
{
    // These new source candidates have their own executable parity proof. They
    // must not be folded into or regenerate the frozen pre-migration inventory.
    private static readonly string[] WorkerSourcePaths =
    [
        "src/Sunder.Package.Agent.Builder/Rpc/BuilderRuntimeService.cs",
        "src/Sunder.Package.Agent.Builder/Rpc/BuilderRuntimeValues.cs",
    ];
    private const string AppSourcePath = "src/Sunder.Package.Agent.Builder/Rpc/BuilderRpcGateway.cs";
    internal static readonly string[] SourcePaths = [.. WorkerSourcePaths, AppSourcePath];

    [BuilderSourceMigrationFact]
    [Trait("Category", "SourceMigration")]
    public async Task BuilderSourcePilot_PreservesFrozenDescriptorAndCompilesGeneratedDispatchWithoutLocalProtocol()
    {
        var root = CanonicalPackageRoles.Source("Builder");
        var baselinePath = Path.Combine(root, "sunder-contracts.owned.json");
        var baseline = File.ReadAllBytes(baselinePath);
        var selected = File.ReadAllBytes(Path.Combine(root, "sunder-contracts.lock.json"));
        using var roles = new CanonicalPackageRoles();
        var assembly = roles.Load("Builder", "worker");
        var app = roles.Load("Builder", "app");
        try
        {
            var generated = CanonicalPackageRoles.OwnedDescriptor(root, "sunder.package.agent.builder.runtime");
            Assert.DoesNotContain(assembly.GetReferencedAssemblies(), value => value.Name == "Sunder.Package.Agent.Builder.Protocol");
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcExecutor"));
            Assert.Null(assembly.GetType("Sunder.Package.Agent.Builder.BuilderRpcProvider"));
            using var state = new RegressionTestRpcState(workerStyleProviderBoundary: true);
            var client = new RegressionTestRpcClient(state, "sunder.package.agent.builder");
            using var package = RegressionTestPackageScope.Create();
            var context = new TestSupport.TestPackageContext("sunder.package.agent.builder", package.RootPath, version: "2.0.0", storage: package.Context.Storage);
            var composition = AgentSourceArtifactMigrationTests.CreateGeneratedWorker(assembly, context, client);
            await using var lifetime = composition.Lifetime;
            var registration = Assert.Single(composition.Options.Providers);
            Assert.Equal("builder.runtime", registration.ProviderId);
            Assert.Equal(generated.Sha256, registration.DescriptorSha256);
            Assert.Equal(generated.AbiSha256, registration.AbiSha256);
            var service = Assert.Single(generated.Services);
            var declaredErrors = registration.DeclaredErrors[service.ServiceId];
            Assert.Equal(service.Methods.Count, declaredErrors.Count);
            foreach (var method in service.Methods)
                Assert.Equal(method.Errors, declaredErrors[method.MethodId]);
            state.Add("sunder.package.agent.builder", generated, registration.Handler, registration.Handler, NoopDisposable.Instance, registration.ProviderId, packageVersion: "2.0.0");
            await AgentSourceArtifactMigrationTests.ActivateGeneratedComposition(composition.Options);
            var provider = Assert.Single((await client.DiscoverAsync(generated.ContractId)).Providers);
            var error = await Assert.ThrowsAsync<SunderRpcException>(async () => await client.InvokeAsync(provider.Endpoint,
                "builder-runtime", "list-workspaces", JsonSerializer.SerializeToElement(new { })));
            Assert.Equal(SunderRpcErrorKind.Domain, error.Error.Kind);
            Assert.Equal("agent.builder.workspace-execution-unavailable", error.Error.Code);
            await VerifyAppAsync(root, app, context, generated, registration, lifetime, state, client);
        }
        finally
        {
            Assert.Equal(baseline, File.ReadAllBytes(baselinePath));
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

    internal static async Task Run(string directory, TimeSpan timeLimit, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet") { WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
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
