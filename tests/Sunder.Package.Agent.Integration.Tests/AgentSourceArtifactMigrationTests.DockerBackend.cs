using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Xml.Linq;
using Microsoft.Extensions.DependencyInjection;
using Sunder.Sdk.Abstractions;
using Sunder.Sdk.Stacks;
using Sunder.Sdk.Worker;
using Xunit;

namespace Sunder.Package.Agent.Tests;

public sealed partial class AgentSourceArtifactMigrationTests
{
    private static async Task<Type> BuildDockerBackend(string root, string worker, AssemblyLoadContext load)
    {
        var directory = Directory.CreateDirectory(Path.Combine(root, "DockerBackend")).FullName;
        var repository = BuilderSourceMigrationTests.FindRepository();
        var references = Directory.GetFiles(SingleProjectRoleOutput(worker, "worker"), "*.dll");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"), new XElement("AssemblyName", "Sunder.Package.Agent.Tests"),
                 new XElement("Nullable", "enable"), new XElement("ImplicitUsings", "enable"), new XElement("TreatWarningsAsErrors", "true")),
            new XElement("ItemGroup", references.Select(path => new XElement("Reference", new XAttribute("Include", Path.GetFileNameWithoutExtension(path)), new XAttribute("HintPath", path))))))
            .Save(Path.Combine(directory, "Backend.csproj"));
        File.Copy(Path.Combine(repository, "tests/Sunder.Package.Agent.Tests/Fixtures/DockerArtifactRunner.cs.txt"), Path.Combine(directory, "Backend.cs"));
        WriteControlledWorkerEntry(worker, directory);
        await Run(directory, "restore", "Backend.csproj", "--disable-parallel");
        await Run(directory, "build", "Backend.csproj", "--no-restore");
        using var stream = File.OpenRead(Path.Combine(directory, "bin/Debug/net10.0/Sunder.Package.Agent.Tests.dll"));
        return load.LoadFromStream(stream).GetType("ArtifactDockerCli", true)!;
    }

    private sealed class DockerArtifactBackend
    {
        private readonly Dictionary<string, string> _mounts = new(StringComparer.Ordinal);
        private string? _signature;
        internal List<string[]> Calls { get; } = [];
        internal int Challenges;
        internal int Stops;
        internal TaskCompletionSource Blocked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string ImageIdentity = "sha256:" + new string('a', 64);
        internal async Task<(int, string, bool, bool)> Run(string[] args, int timeout, CancellationToken token, string? input)
        {
            token.ThrowIfCancellationRequested();
            Calls.Add(args);
            if (args[0] is "version" or "pull") return (0, "Controlled Docker", false, false);
            if (args[0] == "info") return (0, "controlled-daemon", false, false);
            if (args[0] == "image" && args[1] == "inspect") return (0, ImageIdentity, false, false);
            if (args[0] == "inspect") return _signature is null ? (1, "", false, false) : (0, "true " + _signature, false, false);
            if (args[0] == "run")
            {
                Assert.Contains("--init", args);
                Assert.Equal("none", Option("--network")); Assert.Equal("ALL", Option("--cap-drop"));
                Assert.Equal("no-new-privileges=true", Option("--security-opt")); Assert.Equal("never", Option("--pull"));
                Assert.False(string.IsNullOrWhiteSpace(Option("--user")));
                _signature = Option("--label").Split('=', 2)[1];
                _mounts.Clear();
                for (var index = 0; index < args.Length - 1; index++)
                    if (args[index] == "--mount")
                    {
                        var fields = args[index + 1].Split(',').Select(part => part.Split('=', 2)).ToDictionary(pair => pair[0], pair => pair[1], StringComparer.Ordinal);
                        Assert.Equal("bind", fields["type"]); _mounts[fields["target"]] = fields["source"];
                    }
                return (0, "controlled-container", false, false);
                string Option(string key) => args[Array.IndexOf(args, key) + 1];
            }
            if (args[0] == "rm") { _signature = null; return (0, "", false, false); }
            if (args[0] == "stop") { Stops++; return (0, "", false, false); }
            if (args[0] == "start") return (0, "", false, false);
            if (args[0] == "exec")
            {
                var command = args[^1];
                if (command.StartsWith("set -eu", StringComparison.Ordinal) && command.Contains("host-challenge", StringComparison.Ordinal))
                {
                    Challenges++;
                    foreach (var pair in _mounts.OrderByDescending(pair => pair.Key.Length)) command = command.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                    var start = new ProcessStartInfo("/bin/sh") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
                    start.ArgumentList.Add("-c"); start.ArgumentList.Add(command);
                    using var process = Process.Start(start)!;
                    var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
                    try { await process.WaitForExitAsync(token).WaitAsync(TimeSpan.FromSeconds(5)); }
                    finally { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); } }
                    return (process.ExitCode, await output + await error, false, false);
                }
                if (command.Contains("artifact-block", StringComparison.Ordinal)) { Blocked.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                return (0, "controlled-output", false, false);
            }
            throw new InvalidOperationException("Unexpected controlled Docker command: " + string.Join(' ', args));
        }
    }
}
