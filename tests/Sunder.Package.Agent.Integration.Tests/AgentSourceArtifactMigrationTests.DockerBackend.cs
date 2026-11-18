using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text;
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
        var diagnostics = await Run(directory, "build", "Backend.csproj", "--no-restore");
        using var stream = File.OpenRead(BuilderSourceMigrationTests.RequireProbeOutput(directory, "Sunder.Package.Agent.Tests", diagnostics));
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
            if (args[0] == "info") return (0, "controlled-daemon linux", false, false);
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
                if (command.StartsWith("set -eu", StringComparison.Ordinal))
                {
                    Challenges++;
                    foreach (var pair in _mounts.OrderByDescending(pair => pair.Key.Length)) command = command.Replace(pair.Key, pair.Value, StringComparison.Ordinal);
                    // The controlled container runs this narrow protocol against
                    // the mapped host files. Windows cannot launch /bin/sh; its
                    // real retained-object challenge still verifies every read,
                    // response and unlink performed by this fixture interpreter.
                    if (OperatingSystem.IsWindows())
                    {
                        try { RunWindowsChallenge(command); }
                        catch (Exception challengeError)
                        {
                            throw new InvalidOperationException("Controlled Docker challenge failed: " + challengeError.Message, challengeError);
                        }
                        return (0, "", false, false);
                    }
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

        private static void RunWindowsChallenge(string command)
        {
            foreach (var step in command.Split(" && ", StringSplitOptions.None))
            {
                if (step is "set -eu" or "umask 077") continue;
                var words = QuotedWords(step);
                if (step.StartsWith("test \"$(cat -- ", StringComparison.Ordinal))
                {
                    Assert.Equal(2, words.Count);
                    Assert.Equal($"test \"$(cat -- {Quote(words[0])})\" = {Quote(words[1])}", step);
                    // Model the container's read without denying the host's
                    // retained write/sanitization handle. File.ReadAllText uses
                    // FileShare.Read and would fail before the real challenge.
                    using var input = new FileStream(words[0], FileMode.Open, FileAccess.Read,
                        FileShare.ReadWrite | FileShare.Delete);
                    using var reader = new StreamReader(input);
                    Assert.Equal(words[1], reader.ReadToEnd());
                }
                else if (step.StartsWith("mkdir -- ", StringComparison.Ordinal))
                {
                    var path = Assert.Single(words);
                    Assert.Equal("mkdir -- " + Quote(path), step);
                    Assert.False(Directory.Exists(path));
                    Directory.CreateDirectory(path);
                }
                else if (step.StartsWith("printf '%s' ", StringComparison.Ordinal))
                {
                    Assert.Equal(3, words.Count);
                    Assert.Equal("%s", words[0]);
                    Assert.Equal($"printf '%s' {Quote(words[1])} > {Quote(words[2])}", step);
                    Assert.False(File.Exists(words[2]));
                    File.WriteAllText(words[2], words[1]);
                }
                else if (step.StartsWith("rm -f -- ", StringComparison.Ordinal))
                {
                    Assert.NotEmpty(words);
                    Assert.Equal("rm -f -- " + string.Join(' ', words.Select(Quote)), step);
                    foreach (var path in words) File.Delete(path);
                }
                else if (step.StartsWith("rmdir -- ", StringComparison.Ordinal))
                {
                    var path = Assert.Single(words);
                    Assert.Equal("rmdir -- " + Quote(path), step);
                    Directory.Delete(path, recursive: false);
                }
                else Assert.Fail("Unexpected controlled mount challenge step: " + step);
            }

            static string Quote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

            static IReadOnlyList<string> QuotedWords(string text)
            {
                var words = new List<string>();
                var offset = 0;
                while ((offset = text.IndexOf('\'', offset)) >= 0)
                {
                    offset++;
                    var word = new StringBuilder();
                    while (true)
                    {
                        var end = text.IndexOf('\'', offset);
                        Assert.True(end >= offset, "Incomplete quoted challenge argument.");
                        word.Append(text.AsSpan(offset, end - offset));
                        offset = end + 1;
                        if (!text.AsSpan(offset).StartsWith("\"'\"'", StringComparison.Ordinal)) break;
                        word.Append('\'');
                        offset += 4;
                    }
                    words.Add(word.ToString());
                }
                return words;
            }
        }
    }
}
