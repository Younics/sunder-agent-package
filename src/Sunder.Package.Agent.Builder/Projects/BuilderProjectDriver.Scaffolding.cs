using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderProjectDriver
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> CacheGates = new(StringComparer.Ordinal);

    private async Task CreateAsync(BuilderJobRequest request, IBuilderJobReporter reporter, CancellationToken token)
    {
        var destination = request.ProjectFolder!;
        var parent = Path.GetDirectoryName(destination)!;
        BuilderProjectSource.EnsureSafePath(parent, destination);
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("Select an existing parent directory for the new project.");
        RequireEmptyDestination(destination);
        if (request.Kind == BuilderProjectKind.Dotnet) _ = BuilderTemplateCache.ValidateBasePath(options);
        await using var selection = await toolchains.SelectAsync(request.Kind, null, request.Installation, new ToolchainStatusProgress(reporter), token).ConfigureAwait(false);
        reporter.Update(new() { Installation = selection.Installation, Message = "Scaffolding the coordinated Sunder UI template." });
        var staging = Path.Combine(parent, ".sunder-builder-create-" + Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "project");
        BuilderToolchainFiles.CreatePrivateDirectory(staging);
        try
        {
            if (request.Kind == BuilderProjectKind.Dotnet)
            {
                // Select the prepared SDK before even the template installation/list commands.
                await WriteSdkPinAsync(staging, selection.Installation.Version, token).ConfigureAwait(false);
                using var template = await EnsureDotnetTemplateAsync(selection, staging, reporter, token).ConfigureAwait(false);
                reporter.Update(new() { Message = "Creating the .NET package source." });
                await RunAsync(selection, ["new", "sunder-package", "--name", ProjectName(request.DisplayName!),
                    "--packageId", request.PackageId!, "--packageName", request.DisplayName!, "--withAvalonia", "true", "--output", payload],
                    staging, reporter, token, cliHome: template.HomePath).ConfigureAwait(false);
                await WriteSdkPinAsync(payload, selection.Installation.Version, token).ConfigureAwait(false);
            }
            else
            {
                var creator = await EnsureNodeCreatorAsync(selection, reporter, token).ConfigureAwait(false);
                await RunAsync(selection, [creator, "--output", payload, "--template", "react-node", "--language", "ts", "--package-id", request.PackageId!,
                    "--package-name", request.DisplayName!, "--yes"], staging, reporter, token).ConfigureAwait(false);
                await File.WriteAllTextAsync(Path.Combine(payload, ".node-version"), selection.Installation.Version + "\n", token).ConfigureAwait(false);
                var manifestPath = Path.Combine(payload, "package.json");
                var manifest = JsonNode.Parse(await File.ReadAllTextAsync(manifestPath, token).ConfigureAwait(false))!.AsObject();
                manifest["packageManager"] = "npm@" + selection.Installation.NpmVersion;
                await File.WriteAllTextAsync(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", token).ConfigureAwait(false);
                await ApplyNodeOverridesAsync(payload, token).ConfigureAwait(false);
            }
            var requirements = BuilderToolchainRequirements.Read(request.Kind, payload);
            if (!requirements.Allows(request.Kind, selection.Installation.Version, selection.Installation.NpmVersion))
                throw new InvalidOperationException("The prepared SDK does not satisfy the generated project's engines or inherited version pins. No destination was replaced.");
            var candidates = await discovery.DiscoverAsync(payload, token).ConfigureAwait(false);
            var generated = candidates.Where(item => item.Kind == request.Kind && BuilderJobValidation.Same(item.ProjectFolder, payload)
                && item.PackageId == request.PackageId && item.DisplayName == request.DisplayName).ToArray();
            if (generated.Length != 1) throw new InvalidDataException("The template did not produce exactly one matching canonical project.");
            var relativeEntry = Path.GetRelativePath(payload, generated[0].EntryPoint);
            // Empty destinations are removed nonrecursively. A concurrent user write makes this fail,
            // and Directory.Move never merges or overwrites an existing source directory.
            BuilderProjectSource.EnsureSafePath(parent, destination);
            RequireEmptyDestination(destination);
            token.ThrowIfCancellationRequested();
            if (Directory.Exists(destination)) Directory.Delete(destination, recursive: false);
            Directory.Move(payload, destination);
            var committed = generated[0] with { ProjectFolder = destination, EntryPoint = Path.Combine(destination, relativeEntry) };
            reporter.Update(new() { Project = committed, Message = "Source created. Setup is pending." });
            committed = await EvaluateTargetAsync(committed, selection, reporter, token).ConfigureAwait(false);
            var development = BuilderProjectDriverOutputs.DevelopmentFolder(committed, "Debug");
            // Publish recoverable state immediately after the atomic source commit, before restore.
            reporter.Update(new() { Project = committed, DevelopmentFolder = development, Message = "Source created. Restoring dependencies and preparing the first development build." });
            token.ThrowIfCancellationRequested();
            committed = (await discovery.InspectAsync(committed.EntryPoint, token).ConfigureAwait(false)) with { TargetFramework = committed.TargetFramework };
            reporter.Update(new() { Project = committed });
            await RestoreAsync(committed, selection, reporter, token).ConfigureAwait(false);
            await BuildAsync(committed, selection, reporter, requireApp: true, token, restore: false).ConfigureAwait(false);
            reporter.Update(new() { Message = "Project created; its development package is ready." });
        }
        finally
        {
            try { BuilderToolchainFiles.DeleteTree(staging); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { reporter.Report($"\nTemporary scaffold cleanup failed at {staging}: {error.Message}\n"); }
        }
    }

    private async Task<BuilderTemplateCache> EnsureDotnetTemplateAsync(IBuilderProjectDriverSelection selection, string workingDirectory,
        IBuilderJobReporter reporter, CancellationToken token)
    {
        var cache = await BuilderTemplateCache.AcquireAsync(options, selection.Installation.Version, token).ConfigureAwait(false);
        try
        {
            if (!cache.IsReady())
            {
                reporter.Update(new() { Message = "Preparing the Sunder .NET project template." });
                var package = options.DotnetTemplatePackagePath ?? "Sunder.Package.Templates@" + options.CoordinatedPackageVersion;
                await RunAsync(selection, ["new", "install", package, "--force"], workingDirectory, reporter, token, cliHome: cache.HomePath).ConfigureAwait(false);
                var listing = await RunAsync(selection, ["new", "list", "sunder-package"], workingDirectory, reporter, token, cliHome: cache.HomePath).ConfigureAwait(false);
                if (!listing.StandardOutput.Contains("sunder-package", StringComparison.Ordinal)) throw new InvalidDataException("The coordinated package did not install the Sunder template.");
                await cache.MarkReadyAsync(token).ConfigureAwait(false);
            }
            return cache;
        }
        catch { cache.Dispose(); throw; }
    }

    private async Task<string> EnsureNodeCreatorAsync(IBuilderProjectDriverSelection selection, IBuilderJobReporter reporter, CancellationToken token)
    {
        var identity = await CacheIdentityAsync(options.NodeCreatorPackagePath, token).ConfigureAwait(false);
        var cache = Path.Combine(options.CacheRootPath, "node-creators", identity);
        using var cacheLock = await LockCacheAsync(cache, token).ConfigureAwait(false);
        var installed = Path.Combine(cache, "content");
        var cli = Path.Combine(installed, "node_modules", "create-sunder-package", "dist", "cli.js");
        if (await IsCreatorAsync(installed, token).ConfigureAwait(false)) return cli;
        var staging = Path.Combine(cache, "stage-" + Guid.NewGuid().ToString("N"));
        BuilderToolchainFiles.CreatePrivateDirectory(staging);
        try
        {
            var sdk = options.NodePackageOverrides.TryGetValue("@sunder/sdk", out var localSdk) ? "file:" + localSdk.Replace('\\', '/') : options.CoordinatedPackageVersion;
            var creator = options.NodeCreatorPackagePath is { } localCreator ? "file:" + localCreator.Replace('\\', '/') : options.CoordinatedPackageVersion;
            await BuilderToolchainFiles.WriteJsonAsync(Path.Combine(staging, "package.json"), new
            {
                name = "sunder-builder-creator-cache", version = "1.0.0", @private = true,
                dependencies = new Dictionary<string, string> { ["create-sunder-package"] = creator, ["@sunder/sdk"] = sdk },
            }, token).ConfigureAwait(false);
            await RunAsync(selection, ["install", "--no-audit", "--no-fund", "--ignore-scripts"], staging, reporter, token, npm: true).ConfigureAwait(false);
            if (!await IsCreatorAsync(staging, token).ConfigureAwait(false)) throw new InvalidDataException("The creator cache does not contain the exact coordinated creator and SDK versions.");
            token.ThrowIfCancellationRequested();
            BuilderToolchainFiles.DeleteTree(installed);
            Directory.Move(staging, installed);
            return cli;
        }
        finally { BuilderToolchainFiles.DeleteTree(staging); }
    }

    private async Task<bool> IsCreatorAsync(string directory, CancellationToken token)
    {
        try
        {
            foreach (var name in new[] { "create-sunder-package", "@sunder/sdk" })
            {
                var path = Path.Combine(directory, "node_modules", name, "package.json");
                BuilderProjectSource.EnsureSafePath(directory, path);
                if (!File.Exists(path) || new FileInfo(path).Length > 64 * 1024) return false;
                using var json = BuilderProjectSource.Json(await File.ReadAllBytesAsync(path, token).ConfigureAwait(false));
                if (json.RootElement.GetProperty("name").GetString() != name || json.RootElement.GetProperty("version").GetString() != options.CoordinatedPackageVersion) return false;
            }
            var cli = Path.Combine(directory, "node_modules", "create-sunder-package", "dist", "cli.js");
            BuilderProjectSource.EnsureSafePath(directory, cli);
            return File.Exists(cli);
        }
        catch (Exception error) when (error is IOException or InvalidDataException or JsonException or InvalidOperationException or KeyNotFoundException) { return false; }
    }

    private async Task ApplyNodeOverridesAsync(string project, CancellationToken token)
    {
        if (options.NodePackageOverrides.Count == 0) return;
        var path = Path.Combine(project, "package.json");
        var json = JsonNode.Parse(await File.ReadAllTextAsync(path, token).ConfigureAwait(false))!.AsObject();
        foreach (var (name, file) in options.NodePackageOverrides)
        {
            if (!File.Exists(file)) throw new FileNotFoundException("Local Node package override is missing.", file);
            var section = name == "@sunder/sdk" ? "dependencies" : "devDependencies";
            json[section]!.AsObject()[name] = "file:" + file.Replace('\\', '/');
        }
        await File.WriteAllTextAsync(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", token).ConfigureAwait(false);
    }

    private async Task<string> CacheIdentityAsync(string? package, CancellationToken token)
    {
        var values = new List<string> { options.CoordinatedPackageVersion, options.NodeRegistryUri ?? "" };
        foreach (var path in new[] { package }.Concat(options.NodePackageOverrides.OrderBy(item => item.Key, StringComparer.Ordinal).Select(item => item.Value)).Where(path => path is not null))
        {
            if (!File.Exists(path) || new FileInfo(path).Length > 512L * 1024 * 1024) throw new IOException("Local tooling packages must be existing archives smaller than 512 MiB.");
            await using var stream = File.OpenRead(path!);
            values.Add(Convert.ToHexString(await SHA256.HashDataAsync(stream, token).ConfigureAwait(false)));
        }
        return options.CoordinatedPackageVersion + "-" + Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', values))))[..16];
    }

    private async Task<IDisposable> LockCacheAsync(string directory, CancellationToken token)
    {
        BuilderProjectSource.EnsureSafePath(options.CacheRootPath, directory);
        BuilderToolchainFiles.CreatePrivateDirectory(directory);
        var semaphore = CacheGates.GetOrAdd(directory, static _ => new(1, 1));
        await semaphore.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var path = Path.Combine(directory, ".sunder-builder-cache.lock");
            while (true)
            {
                token.ThrowIfCancellationRequested();
                BuilderProjectSource.EnsureSafePath(directory, path);
                try { return new CacheLease(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None), semaphore); }
                catch (IOException) { await Task.Delay(150, token).ConfigureAwait(false); }
            }
        }
        catch { semaphore.Release(); throw; }
    }

    private static Task WriteSdkPinAsync(string directory, string version, CancellationToken token) => BuilderToolchainFiles.WriteJsonAsync(
        Path.Combine(directory, "global.json"), new { sdk = new { version, rollForward = "disable", allowPrerelease = false, paths = new[] { "$host$" } } }, token);

    private static void RequireEmptyDestination(string path)
    {
        if (File.Exists(path) || Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
            throw new IOException("The destination contains user files. Choose an absent or empty directory; use setup for an existing project.");
    }

    private static string ProjectName(string name)
    {
        var value = new string(name.Where(char.IsAsciiLetterOrDigit).Take(64).ToArray());
        return value.Length == 0 ? "SunderPackage" : char.IsAsciiDigit(value[0]) ? "Package" + value : value;
    }

    private sealed class CacheLease(FileStream stream, SemaphoreSlim semaphore) : IDisposable
    {
        public void Dispose() { stream.Dispose(); semaphore.Release(); }
    }
}
