namespace Sunder.Package.Agent.Builder;

public sealed class BuilderProjectDiscovery
{
    /// <summary>Searches the root and at most four nested levels; returns every genuine source candidate.</summary>
    public async Task<IReadOnlyList<BuilderProjectInspection>> DiscoverAsync(string folder, CancellationToken cancellationToken = default)
    {
        folder = Path.GetFullPath(folder);
        BuilderProjectSource.EnsureSafePath(folder, folder);
        if (!Directory.Exists(folder)) throw new DirectoryNotFoundException(folder);
        var pending = new Queue<(string Path, int Depth)>(); pending.Enqueue((folder, 0));
        var result = new List<BuilderProjectInspection>();
        var count = 0;
        var entries = 0;
        while (pending.TryDequeue(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++count > 256) throw new InvalidDataException("Discovery exceeds 256 folders. Select a narrower project folder.");
            foreach (var path in Directory.EnumerateFileSystemEntries(directory.Path).Take(8193 - entries).Order(StringComparer.Ordinal))
            {
                if (++entries > 8192) throw new InvalidDataException("Discovery exceeds 8,192 entries. Select a narrower project folder.");
                var attributes = File.GetAttributes(path);
                if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0) continue;
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    if (directory.Depth < 4 && !BuilderProjectSource.OutputFolders.Contains(Path.GetFileName(path))) pending.Enqueue((path, directory.Depth + 1));
                }
                else if (Path.GetFileName(path) == "sunder.package.json" || path.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                {
                    try { result.Add(await InspectAsync(path, cancellationToken).ConfigureAwait(false)); }
                    catch (NotSunderProjectException) { }
                    if (result.Count > 64) throw new InvalidDataException("Discovery exceeds 64 package candidates. Select a narrower folder.");
                }
            }
        }
        return result;
    }

    public async Task<BuilderProjectInspection> InspectAsync(string entryPoint, CancellationToken cancellationToken = default)
    {
        entryPoint = Path.GetFullPath(entryPoint);
        if (!File.Exists(entryPoint)) throw new FileNotFoundException("Select a project entry point.", entryPoint);
        var kind = Path.GetFileName(entryPoint) == "sunder.package.json" ? BuilderProjectKind.Node
            : entryPoint.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ? BuilderProjectKind.Dotnet
            : throw new NotSunderProjectException("Select a .csproj or sunder.package.json entry point.");
        var source = await BuilderProjectSource.CaptureAsync(Path.GetDirectoryName(entryPoint)!, cancellationToken).ConfigureAwait(false);
        return Inspect(source, entryPoint, kind);
    }

    internal static BuilderProjectInspection Inspect(BuilderProjectSource source, string entryPoint, BuilderProjectKind kind)
    {
        var metadata = kind == BuilderProjectKind.Dotnet ? BuilderDotnetMetadata.Inspect(source, entryPoint) : BuilderNodeMetadata.Inspect(source);
        var reason = source.UnsafeReason ?? metadata.ReadOnlyReason;
        var idReason = reason ?? BuilderProjectHistory.IdEditReason(source, kind == BuilderProjectKind.Node ? metadata.PackageId : null) ?? metadata.IdReadOnlyReason;
        return new()
        {
            Kind = kind, ProjectFolder = source.Root, EntryPoint = entryPoint,
            DisplayName = metadata.DisplayName, PackageId = metadata.PackageId, SourceRevision = source.Revision,
            CanEditDisplayName = reason is null, CanEditPackageId = idReason is null,
            ReadOnlyReason = reason, PackageIdReadOnlyReason = idReason,
            RequiresNodeToolingForIdEdit = kind == BuilderProjectKind.Node && Directory.Exists(Path.Combine(source.Root, ".sunder"))
        };
    }
}

internal sealed class NotSunderProjectException(string message) : IOException(message);
internal sealed record BuilderSourceMetadata(string DisplayName, string PackageId, string? ReadOnlyReason = null, string? IdReadOnlyReason = null);
