using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

/// <summary>A local feed explicitly selects one archive per identity, not a newest or coordinated version.</summary>
internal static class BuilderLocalPackageFeed
{
    internal sealed record Archive(string Id, string Version, string Name, string Path);

    internal static Archive[] Select(string source, IReadOnlyList<string> ids, bool npm)
    {
        var files = Directory.EnumerateFileSystemEntries(source).Take(4097).ToArray();
        if (files.Length > 4096) throw new InvalidDataException("Local package feed exceeds 4,096 top-level entries.");
        return ids.Select(id =>
        {
            var prefix = npm ? id.TrimStart('@').Replace('/', '-') + "-" : id + ".";
            var suffix = npm ? ".tgz" : ".nupkg";
            var matches = files.Select(path => (Path: path, Name: System.IO.Path.GetFileName(path)))
                .Where(item => item.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && item.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                .Select(item => new Archive(id, item.Name[prefix.Length..^suffix.Length], item.Name, item.Path))
                .Where(item => SemanticVersion.TryParse(item.Version, out _)).ToArray();
            if (matches.Length != 1) throw new InvalidDataException($"Local package feed requires exactly one flat {id} archive (found {matches.Length}).");
            return matches[0];
        }).ToArray();
    }
}
