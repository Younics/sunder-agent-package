using System.Diagnostics;
using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

/// <summary>Edits authored metadata after a source revision check. The caller owns package unloading.</summary>
public sealed class BuilderProjectMetadataService(string nodeExecutable = "node", string? nodeToolCliPath = null,
    IReadOnlyDictionary<string, string?>? environment = null, IProgress<string>? progress = null)
{
    public async Task<BuilderProjectInspection> SaveAsync(BuilderProjectInspection expected, string displayName, string packageId, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(expected);
        BuilderProjectSource.ValidateName(displayName); BuilderProjectSource.ValidateId(packageId);
        var root = Path.GetDirectoryName(Path.GetFullPath(expected.EntryPoint))!;
        BuilderProjectSource.EnsureSafePath(root, expected.EntryPoint);
        // Refusals (especially retained public identity/history) do not even create an edit lock file.
        var preflight = await new BuilderProjectDiscovery().InspectAsync(expected.EntryPoint, cancellationToken).ConfigureAwait(false);
        if (preflight.Kind != expected.Kind || Path.GetFullPath(expected.ProjectFolder) != root) throw new ArgumentException("The inspection does not describe its project entry point.", nameof(expected));
        if (preflight.SourceRevision != expected.SourceRevision) throw new BuilderProjectConflictException("Project source changed since inspection. Reload metadata before saving.");
        if (!preflight.CanEditDisplayName) throw new InvalidOperationException(preflight.ReadOnlyReason);
        if (packageId != preflight.PackageId && !preflight.CanEditPackageId) throw new InvalidOperationException(preflight.PackageIdReadOnlyReason);
        await using var lease = await BuilderMetadataTransaction.LeaseAsync(root, cancellationToken).ConfigureAwait(false);
        var source = await BuilderProjectSource.CaptureAsync(root, cancellationToken).ConfigureAwait(false);
        var current = BuilderProjectDiscovery.Inspect(source, expected.EntryPoint, expected.Kind);
        if (current.SourceRevision != expected.SourceRevision || current.PackageId != expected.PackageId || current.DisplayName != expected.DisplayName)
            throw new BuilderProjectConflictException("Project source or retained evidence changed since inspection. Reload metadata before saving.");
        if (!current.CanEditDisplayName) throw new InvalidOperationException(current.ReadOnlyReason);
        if (packageId != current.PackageId && !current.CanEditPackageId) throw new InvalidOperationException(current.PackageIdReadOnlyReason);
        if (packageId == current.PackageId && displayName == current.DisplayName) return current;
        var edits = expected.Kind == BuilderProjectKind.Dotnet ? BuilderDotnetMetadata.Plan(source, current, displayName, packageId) : BuilderNodeMetadata.Plan(source, current, displayName, packageId);
        if ((await BuilderProjectSource.CaptureAsync(root, cancellationToken).ConfigureAwait(false)).Revision != source.Revision)
            throw new BuilderProjectConflictException("Project source changed while planning metadata edits.");
        if (expected.Kind == BuilderProjectKind.Node && packageId != current.PackageId && current.RequiresNodeToolingForIdEdit)
            await SaveGeneratedNodeAsync(source, current, edits, cancellationToken).ConfigureAwait(false);
        else
            await BuilderMetadataTransaction.CommitAsync(root, Path.GetFileName(current.EntryPoint), source, edits, cancellationToken).ConfigureAwait(false);
        return await new BuilderProjectDiscovery().InspectAsync(expected.EntryPoint, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Rolls back an interrupted save only if every target still equals its recorded old or proposed bytes.</summary>
    public async Task RecoverAsync(string entryPoint, CancellationToken cancellationToken = default)
    {
        var root = Path.GetDirectoryName(Path.GetFullPath(entryPoint))!;
        await using var lease = await BuilderMetadataTransaction.LeaseAsync(root, cancellationToken).ConfigureAwait(false);
        await BuilderMetadataTransaction.RecoverAsync(root, Path.GetFileName(entryPoint), cancellationToken).ConfigureAwait(false);
    }

    private async Task SaveGeneratedNodeAsync(BuilderProjectSource source, BuilderProjectInspection current, Dictionary<string, byte[]> edits, CancellationToken token)
    {
        var cli = nodeToolCliPath ?? Path.Combine(source.Root, "node_modules", "@sunder", "package-tool", "dist", "cli.js");
        if (!File.Exists(cli)) throw new InvalidOperationException("This already-generated Node project needs @sunder/package-tool with 'metadata edit' support. Install the project tooling, then retry. No source files were changed.");
        var request = new
        {
            schemaVersion = 1, expectedPackageId = current.PackageId,
            expectedFiles = source.Files.Select(file => new { path = file.Key, sha256 = BuilderProjectSource.Hash(file.Value.Bytes) }).ToArray(),
            edits = edits.Select(file => new { path = file.Key, beforeBase64 = Convert.ToBase64String(source.Files[file.Key].Bytes), afterBase64 = Convert.ToBase64String(file.Value) }).ToArray()
        };
        var start = new ProcessStartInfo(nodeExecutable) { WorkingDirectory = source.Root, UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        start.ArgumentList.Add(cli); start.ArgumentList.Add("metadata"); start.ArgumentList.Add("edit"); start.ArgumentList.Add("--project"); start.ArgumentList.Add(source.Root);
        if (environment is not null)
            foreach (var (key, value) in environment)
                if (value is null) start.Environment.Remove(key); else start.Environment[key] = value;
        token.ThrowIfCancellationRequested();
        using var process = Process.Start(start) ?? throw new IOException("Could not start Node metadata tooling.");
        var output = ReadOutputAsync(process.StandardOutput, token); var error = ReadOutputAsync(process.StandardError, token);
        try
        {
            await process.StandardInput.WriteAsync(JsonSerializer.Serialize(request).AsMemory(), token).ConfigureAwait(false);
            process.StandardInput.Close();
            await process.WaitForExitAsync(token).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException("Node metadata edit failed: " + await error.ConfigureAwait(false));
            _ = await output.ConfigureAwait(false);
        }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            try { await Task.WhenAll(output, error).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            throw;
        }
    }

    private async Task<string> ReadOutputAsync(StreamReader reader, CancellationToken token)
    {
        const int maximum = 16 * 1024;
        var tail = new System.Text.StringBuilder();
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer, token).ConfigureAwait(false) is var count && count != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > maximum) tail.Remove(0, tail.Length - maximum);
            progress?.Report(new string(buffer, 0, count));
        }
        return tail.ToString();
    }
}
