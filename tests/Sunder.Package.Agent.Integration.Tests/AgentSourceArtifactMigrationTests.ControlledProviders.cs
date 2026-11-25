using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using Sunder.Sdk.Rpc;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static readonly ConcurrentDictionary<string, Lazy<Task<string>>> ControlledProviderBuilds = new(StringComparer.Ordinal);

    // A normal imported-provider compilation, cached by every material input. The
    // fixture has no Agent implementation reference or authored transport handler.
    internal static Task<string> BuildControlledProvidersAsync(string root, string core, string driver, string archive, string cache)
    {
        var files = new[] { "ControlledNaturalTarget.cs.txt", "ControlledNaturalTools.cs.txt", "ControlledNaturalObservedTools.cs.txt", "ControlledStrictReferenceService.cs.txt" };
        var fixtureRoot = Path.Combine(RegressionTestRpcDescriptors.RepositoryRoot, "tests/Sunder.Package.Agent.Tests/Fixtures");
        var source = files.Select(name => File.ReadAllText(Path.Combine(fixtureRoot, name))).ToArray();
        var identity = Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(archive))) + "\n" + string.Join('\n', source);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        var directory = Path.Combine(root, "ControlledProviders-" + hash);
        return ControlledProviderBuilds.GetOrAdd(directory, _ => new(() => BuildAsync(), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

        async Task<string> BuildAsync()
        {
            Directory.CreateDirectory(directory);
            WriteImportProject(directory, core, driver, cache, archive, provider: true,
                packageId: "example.agent.controlled", assemblyName: "ControlledNaturalProviders",
                domainReferences: [typeof(Microsoft.Extensions.DependencyInjection.ServiceProvider).Assembly.Location]);
            CopyMinimalContractFixture(core, directory);
            File.WriteAllText(Path.Combine(directory, "Source.cs"), "public sealed class Seed;");
            await Run(directory, "restore", "Imported.csproj", "--disable-parallel", "-p:BuildProjectReferences=false");
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Accept", "-p:BuildProjectReferences=false");
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Freeze", "-p:BuildProjectReferences=false");
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Add", "-p:BuildProjectReferences=false");
            var contracts = new[] { RegressionTestBindings.ExecutionTargets, RegressionTestBindings.ToolSources, RegressionTestBindings.ObservedToolExecutions };
            var keys = new[] { "controlledTarget", "controlledTools", "controlledObservedTools" };
            for (var index = 0; index < contracts.Length; index++)
            {
                // Each Implement validates a complete source provider against the
                // selected canonical contract; no lock/descriptor is hand-authored.
                File.WriteAllText(Path.Combine(directory, files[index][..^4]), source[index]);
                await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Implement",
                    "-p:ContractId=" + contracts[index], "-p:ProviderKey=" + keys[index], "-p:BuildProjectReferences=false");
            }
            File.WriteAllText(Path.Combine(directory, files[^1][..^4]), source[^1]);
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Accept", "-p:BuildProjectReferences=false");
            await Run(directory, "msbuild", "Imported.csproj", "-t:FixtureOperation", "-p:Operation=Freeze", "-p:BuildProjectReferences=false");
            var selected = File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json"));
            await Run(directory, "build", "Imported.csproj", "--no-restore", "-p:BuildProjectReferences=false", "-p:GeneratePackageRoles=true");
            Assert.Equal(selected, File.ReadAllBytes(Path.Combine(directory, "sunder-contracts.lock.json")));
            VerifyStrictReferenceMetadata(directory);
            return Path.Combine(directory, "bin", BuilderSourceMigrationTests.FixtureConfiguration, "net10.0/ControlledNaturalProviders.dll");
        }
    }

    private static void VerifyStrictReferenceMetadata(string directory)
    {
        var source = File.ReadAllText(Path.Combine(directory, "ControlledStrictReferenceService.cs"));
        Assert.Contains("required ServiceReference<IExecutionTargetService> Target", source, StringComparison.Ordinal);
        Assert.DoesNotContain("SunderOptionalServiceReference", source, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(directory, "obj", BuilderSourceMigrationTests.FixtureConfiguration, "net10.0/sunder-rpc/authoring-receipt.v1.json")));
        var strict = Assert.Single(receipt.RootElement.GetProperty("contracts").EnumerateArray(),
            contract => contract.GetProperty("contractId").GetString() == "example.agent.controlled.strict.reference");
        var projection = strict.GetProperty("modelProjection");
        Assert.False(projection.TryGetProperty("optionalServiceReferenceMembers", out _));
        using var descriptor = JsonDocument.Parse(strict.GetProperty("canonicalDescriptor").GetString()!);
        var request = descriptor.RootElement.GetProperty("$defs").GetProperty("StrictReferenceRequest");
        Assert.Contains(request.GetProperty("required").EnumerateArray(), item => item.GetString() == "target");
        Assert.True(request.GetProperty("properties").GetProperty("target").TryGetProperty("$ref", out _));
    }

    internal static async Task VerifyControlledReferenceParticipantsAsync(string root, string core, string driver, string archive, string cache)
    {
        var path = await BuildControlledProvidersAsync(root, core, driver, archive, cache);
        await GeneratedReferenceParticipantProbe.VerifyAsync(path);
    }
}
