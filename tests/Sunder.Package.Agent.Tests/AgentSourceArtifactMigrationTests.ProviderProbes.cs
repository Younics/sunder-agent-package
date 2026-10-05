using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Security;
using Microsoft.Extensions.AI;
using Sunder.Sdk.Rpc;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static Task<Assembly> BuildProviderRoleProbes(string worker, Assembly runtime, string? output = null)
    {
        var family = runtime.GetName().Name!.Split('.').Last();
        if (family is not ("Gemini" or "Anthropic" or "LMStudio" or "OpenAI"))
            return BuildRoleProbes(worker, runtime, "ProviderRoleLocalProbes.cs.txt", [typeof(IChatClient).Assembly.Location]);
        output ??= SingleProjectRoleOutput(worker, "worker");
        return BuildRoleProbes(worker, runtime, "ProviderRoleLocalProbes.cs.txt", Directory.GetFiles(output, "*.dll"),
            assemblyName: "Sunder.Package.Agent.Tests", output: output, controlledComposition: true);
    }

    internal static async Task<Assembly> BuildRoleProbes(string worker, Assembly runtime, string? fixture, string[]? extraReferences = null,
        string? assemblyName = null, string? output = null, bool controlledComposition = false)
    {
        var directory = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(worker)!,
            Path.GetFileName(worker) + (controlledComposition ? "ControlledProbes" : "Probes"))).FullName;
        if (fixture is not null)
        {
            var source = Path.Combine(BuilderSourceMigrationTests.FindRepository(), "tests/Sunder.Package.Agent.Tests/Fixtures", fixture);
            File.Copy(source, Path.Combine(directory, "Probe.cs"));
        }
        if (controlledComposition) WriteControlledWorkerEntry(worker, directory);
        var references = new[]
        {
            Path.Combine(output ?? Path.Combine(worker, "bin/Debug/net10.0", RuntimeInformation.RuntimeIdentifier), runtime.GetName().Name + ".dll"),
            typeof(ISunderRpcClient).Assembly.Location,
        }.Concat(extraReferences ?? []).GroupBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToArray();
        assemblyName ??= runtime.GetName().Name + ".SemanticProbes";
        await File.WriteAllTextAsync(Path.Combine(directory, "Probe.csproj"), $$"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup><TargetFramework>net10.0</TargetFramework><AssemblyName>{{assemblyName}}</AssemblyName><ImplicitUsings>enable</ImplicitUsings><Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup>
              <ItemGroup>{{string.Join("", references.Select(path => $"<Reference Include=\"{SecurityElement.Escape(Path.GetFileNameWithoutExtension(path))}\" HintPath=\"{SecurityElement.Escape(path)}\" />"))}}</ItemGroup>
            </Project>
            """);
        await Run(directory, "restore", "Probe.csproj", "--disable-parallel");
        await Run(directory, "build", "Probe.csproj", "--no-restore");
        using var bytes = File.OpenRead(Path.Combine(directory, "bin/Debug/net10.0", assemblyName + ".dll"));
        var probes = AssemblyLoadContext.GetLoadContext(runtime)!.LoadFromStream(bytes);
        Assert.DoesNotContain(probes.GetReferencedAssemblies(), reference => reference.Name == "Sunder.Package.Agent.Protocol");
        return probes;
    }
}
