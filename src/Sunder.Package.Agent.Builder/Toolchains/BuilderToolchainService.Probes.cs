using System.Runtime.InteropServices;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderToolchainService
{
    /// <summary>Environment changes apply only to the child process. No profile or global PATH is modified.</summary>
    public IReadOnlyDictionary<string, string?> CreateEnvironment(BuilderToolchainInstallation installation)
        => CreateEnvironment(installation, Environment.GetEnvironmentVariable, Environment.GetEnvironmentVariables().Keys.Cast<string>());

    internal IReadOnlyDictionary<string, string?> CreateEnvironment(BuilderToolchainInstallation installation,
        Func<string, string?> readVariable, IEnumerable<string> variableNames)
    {
        if (!Path.IsPathFullyQualified(installation.RootPath) || !Path.IsPathFullyQualified(installation.ExecutablePath) ||
            !BuilderToolchainPolicy.TryVersion(installation.Version, out _)) throw new ArgumentException("Invalid toolchain process identity.", nameof(installation));
        var result = new Dictionary<string, string?>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        string[] cleared = ["DOTNET_HOST_PATH", "MSBuildSDKsPath", "MSBUILD_EXE_PATH", "MSBuildExtensionsPath",
            "DOTNET_STARTUP_HOOKS", "DOTNET_ADDITIONAL_DEPS", "DOTNET_SHARED_STORE", "DOTNET_ROLL_FORWARD",
            "NODE_OPTIONS", "NODE_PATH", "npm_execpath", "npm_node_execpath", "NPM_CONFIG_PREFIX", "NPM_CONFIG_CACHE",
            "NPM_CONFIG_UPDATE_NOTIFIER"];
        foreach (var key in variableNames)
        {
            if (key.StartsWith("DOTNET_ROOT", StringComparison.OrdinalIgnoreCase) ||
                key.StartsWith("DOTNET_MSBUILD_SDK_RESOLVER_", StringComparison.OrdinalIgnoreCase) ||
                cleared.Contains(key, StringComparer.OrdinalIgnoreCase)) result[key] = null;
        }
        foreach (var key in cleared) result[key] = null;
        var bin = Path.GetDirectoryName(installation.ExecutablePath)!;
        var path = (readVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(Path.IsPathFullyQualified);
        // Workers intentionally have no ambient Host PATH. Build children still
        // need the OS shell/utilities used by MSBuild Exec and npm scripts. Add
        // only explicit system locations, never the current directory or a
        // reconstructed user/Host environment; the selected SDK stays first.
        string[] systemPaths;
        if (OperatingSystem.IsWindows())
        {
            var systemRoot = readVariable("SystemRoot") ?? readVariable("WINDIR");
            if (string.IsNullOrWhiteSpace(systemRoot) || !Path.IsPathFullyQualified(systemRoot))
                throw new InvalidOperationException("Windows build children require an absolute SystemRoot or WINDIR.");
            systemPaths = [Path.Combine(systemRoot, "System32"), systemRoot];
        }
        else systemPaths = ["/usr/bin", "/bin", "/usr/sbin", "/sbin"];
        result["PATH"] = string.Join(Path.PathSeparator, new[] { bin }.Concat(path).Concat(systemPaths)
            .Distinct(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        if (installation.Kind == BuilderProjectKind.Dotnet)
        {
            result["DOTNET_ROOT"] = installation.RootPath;
            result["DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant()] = installation.RootPath;
            result["DOTNET_HOST_PATH"] = installation.ExecutablePath;
            result["DOTNET_MULTILEVEL_LOOKUP"] = "0";
            result["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            result["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"] = "1";
            result["DOTNET_NOLOGO"] = "1";
            result["DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE"] = "true";
            result["DOTNET_CLI_HOME"] = Path.Combine(root, "cache", "dotnet-cli");
        }
        else
        {
            result["npm_node_execpath"] = installation.ExecutablePath;
            result["npm_execpath"] = installation.NpmCliPath;
            result["NPM_CONFIG_CACHE"] = Path.Combine(root, "cache", "npm");
            result["NPM_CONFIG_PREFIX"] = Path.Combine(root, "cache", "npm-global", installation.Version);
            result["NPM_CONFIG_UPDATE_NOTIFIER"] = "false";
        }
        return result;
    }

    private async Task<BuilderToolchainInstallation> VerifyAsync(BuilderToolchainInstallation installation, CancellationToken token)
    {
        string? probe = null;
        try
        {
            if (installation.IsManaged)
            {
                BuilderToolchainFiles.CheckPath(root, installation.ExecutablePath);
                if (installation.NpmCliPath is not null) BuilderToolchainFiles.CheckPath(root, installation.NpmCliPath);
            }
            if (!File.Exists(installation.ExecutablePath)) throw new FileNotFoundException("The toolchain executable is missing.", installation.ExecutablePath);
            if (installation.Kind == BuilderProjectKind.Node && (installation.NpmCliPath is null || !File.Exists(installation.NpmCliPath)))
                throw new FileNotFoundException("The complete npm distribution is missing. A Node runtime alone cannot build packages.");
            probe = Path.Combine(root, "probes", Guid.NewGuid().ToString("N"));
            BuilderToolchainFiles.CheckPath(root, probe);
            BuilderToolchainFiles.CreatePrivateDirectory(probe);
            if (installation.Kind == BuilderProjectKind.Dotnet)
            {
                await BuilderToolchainFiles.WriteJsonAsync(Path.Combine(probe, "global.json"), new
                {
                    sdk = new { version = installation.Version, rollForward = "disable", allowPrerelease = false, paths = new[] { "$host$" } },
                }, token).ConfigureAwait(false);
                var list = await RunProbeAsync(installation, ["--list-sdks"], probe, token).ConfigureAwait(false);
                if (list.ExitCode != 0 || !ParseSdks(list.StandardOutput).Any(item => item.Version == installation.Version))
                    throw new InvalidOperationException("The executable did not report the required installed .NET SDK. " + list.CombinedOutput);
            }
            var version = await RunProbeAsync(installation, ["--version"], probe, token).ConfigureAwait(false);
            var actual = version.StandardOutput.Trim();
            if (installation.Kind == BuilderProjectKind.Node) actual = actual.TrimStart('v');
            if (version.ExitCode != 0 || actual != installation.Version)
                throw new InvalidOperationException("The toolchain version smoke check failed. " + version.CombinedOutput);
            if (installation.Kind == BuilderProjectKind.Node)
            {
                var npm = await RunProbeAsync(installation, [installation.NpmCliPath!, "--version"], probe, token).ConfigureAwait(false);
                var npmVersion = npm.StandardOutput.Trim();
                if (npm.ExitCode != 0 || !BuilderToolchainPolicy.TryVersion(npmVersion, out _) ||
                    (installation.NpmVersion is not null && installation.NpmVersion != npmVersion))
                    throw new InvalidOperationException("The bundled npm CLI smoke check failed. " + npm.CombinedOutput);
                installation = installation with { NpmVersion = npmVersion };
            }
            return installation with { IsHealthy = true, HealthError = null };
        }
        catch (Exception ex) when (Recoverable(ex))
        {
            return installation with { IsHealthy = false, HealthError = ex.Message };
        }
        finally { if (probe is not null) Cleanup(probe, null); }
    }

    private Task<BuilderLocalProcessResult> RunProbeAsync(BuilderToolchainInstallation installation,
        IReadOnlyList<string> arguments, string directory, CancellationToken token) => processes.RunAsync(
        new(installation.ExecutablePath, arguments, directory)
        {
            Environment = CreateEnvironment(installation), Timeout = TimeSpan.FromSeconds(30), MaximumCapturedCharacters = 128 * 1024,
        }, cancellationToken: token);

    private async Task<bool> MatchesProjectAsync(BuilderToolchainInstallation installation, string? directory, CancellationToken token)
    {
        if (directory is null || installation.Kind == BuilderProjectKind.Node) return true;
        try
        {
            var selected = await RunProbeAsync(installation, ["--version"], Path.GetFullPath(directory), token).ConfigureAwait(false);
            return selected.ExitCode == 0 && selected.StandardOutput.Trim() == installation.Version;
        }
        catch (Exception ex) when (Recoverable(ex)) { return false; }
    }

    private async Task<IReadOnlyList<BuilderToolchainInstallation>> DiscoverAsync(BuilderProjectKind kind,
        IReadOnlyList<string> candidates, CancellationToken token)
    {
        var result = new List<BuilderToolchainInstallation>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        foreach (var original in candidates.Take(32))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                if (!Path.IsPathFullyQualified(original) || !File.Exists(original)) continue;
                var executable = ResolveExecutable(original);
                // Private roots are enumerated from receipts, never misclassified as external installs.
                if (BuilderToolchainFiles.IsChild(executable, root) || !seen.Add(executable)) continue;
                var bin = Path.GetDirectoryName(executable)!;
                var candidateRoot = kind == BuilderProjectKind.Node && Path.GetFileName(bin) == "bin" ? Path.GetDirectoryName(bin)! : bin;
                var candidate = new BuilderToolchainInstallation
                {
                    Kind = kind, Version = "0.0.0", ExecutablePath = executable, RootPath = candidateRoot,
                    NpmCliPath = kind == BuilderProjectKind.Node ? FindNpm(original, candidateRoot) : null,
                };
                var discovered = await RunProbeAsync(candidate, [kind == BuilderProjectKind.Dotnet ? "--list-sdks" : "--version"],
                    Path.Combine(root, "probes"), token).ConfigureAwait(false);
                if (discovered.ExitCode != 0) continue;
                if (kind == BuilderProjectKind.Dotnet)
                {
                    foreach (var sdk in ParseSdks(discovered.StandardOutput).Take(64))
                    {
                        if (!BuilderToolchainPolicy.IsSupported(kind, sdk.Version)) continue;
                        if (!string.Equals(Path.GetFileName(sdk.SdkPath), "sdk", StringComparison.OrdinalIgnoreCase)) continue;
                        var sdkRoot = Path.GetDirectoryName(sdk.SdkPath)!;
                        var muxer = Path.Combine(sdkRoot, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
                        if (!File.Exists(muxer)) continue;
                        muxer = ResolveExecutable(muxer);
                        if (BuilderToolchainFiles.IsChild(muxer, root)) continue;
                        // Report SDKs, not runtimes. Smoke-test each selected SDK with the exact muxer.
                        result.Add(await VerifyAsync(candidate with { Version = sdk.Version, RootPath = sdkRoot, ExecutablePath = muxer }, token).ConfigureAwait(false));
                    }
                }
                else
                {
                    var version = discovered.StandardOutput.Trim().TrimStart('v');
                    if (BuilderToolchainPolicy.IsSupported(kind, version))
                    {
                        // Version-manager shims are discovery inputs only; builds use Node's actual executable.
                        var resolved = await RunProbeAsync(candidate, ["--print", "process.execPath"], Path.Combine(root, "probes"), token).ConfigureAwait(false);
                        var actual = resolved.StandardOutput.Trim();
                        if (resolved.ExitCode != 0 || !Path.IsPathFullyQualified(actual) || !File.Exists(actual)) continue;
                        actual = ResolveExecutable(actual);
                        if (BuilderToolchainFiles.IsChild(actual, root)) continue;
                        var actualBin = Path.GetDirectoryName(actual)!;
                        var actualRoot = Path.GetFileName(actualBin) == "bin" ? Path.GetDirectoryName(actualBin)! : actualBin;
                        candidate = candidate with { ExecutablePath = actual, RootPath = actualRoot, NpmCliPath = FindNpm(original, actualRoot) };
                        result.Add(await VerifyAsync(candidate with { Version = version }, token).ConfigureAwait(false));
                    }
                }
            }
            catch (Exception ex) when (Recoverable(ex)) { /* One broken external candidate must not hide private installations. */ }
        }
        return result;
    }

    private static IReadOnlyList<string> DiscoverExecutableCandidates(BuilderProjectKind kind)
    {
        var name = kind == BuilderProjectKind.Dotnet ? "dotnet" : "node";
        if (OperatingSystem.IsWindows()) name += ".exe";
        var directories = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(Path.IsPathFullyQualified).ToList();
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (kind == BuilderProjectKind.Dotnet)
        {
            foreach (var variable in new[] { "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64" })
                if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && Path.IsPathFullyQualified(value)) directories.Add(value);
            if (home.Length > 0) directories.Add(Path.Combine(home, ".dotnet"));
            if (OperatingSystem.IsWindows()) directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "dotnet"));
            else directories.AddRange(["/usr/share/dotnet", "/usr/local/share/dotnet", "/opt/dotnet"]);
        }
        else if (OperatingSystem.IsWindows()) directories.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "nodejs"));
        else directories.AddRange(["/usr/local/bin", "/usr/bin", "/opt/homebrew/bin", "/opt/homebrew/opt/node@24/bin"]);
        return directories.Distinct(StringComparer.Ordinal).Select(directory => Path.Combine(directory, name)).ToArray();
    }

    private static string ResolveExecutable(string path)
    {
        var file = new FileInfo(path);
        return file.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? file.FullName;
    }

    private static string? FindNpm(string originalNode, string nodeRoot)
    {
        var paths = new List<string>
        {
            Path.Combine(nodeRoot, "node_modules", "npm", "bin", "npm-cli.js"),
            Path.Combine(nodeRoot, "lib", "node_modules", "npm", "bin", "npm-cli.js"),
        };
        var npm = Path.Combine(Path.GetDirectoryName(originalNode)!, "npm");
        if (File.Exists(npm))
        {
            var target = ResolveExecutable(npm);
            if (Path.GetFileName(target) == "npm-cli.js") paths.Add(target);
        }
        return paths.FirstOrDefault(File.Exists);
    }

    private static IEnumerable<(string Version, string SdkPath)> ParseSdks(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var opening = line.IndexOf(" [", StringComparison.Ordinal);
            if (opening < 1 || !line.EndsWith(']')) continue;
            var version = line[..opening];
            var path = line[(opening + 2)..^1];
            if (BuilderToolchainPolicy.TryVersion(version, out _) && Path.IsPathFullyQualified(path)) yield return (version, path);
        }
    }
}
