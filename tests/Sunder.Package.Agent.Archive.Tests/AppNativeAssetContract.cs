using System.Text.Json;

namespace Sunder.Package.Agent.Tests;

internal static class AppNativeAssetContract
{
    internal static void Verify(JsonElement deps, string entryPoint, string rid, string packageId, IEnumerable<string> projectedEntries)
    {
        var identity = $"{packageId}: app/{rid}";
        var activeTarget = deps.GetProperty("runtimeTarget").GetProperty("name").GetString()!;
        if (!activeTarget.EndsWith("/" + rid, StringComparison.Ordinal))
            throw new InvalidDataException($"{identity}: App dependency graph selects '{activeTarget}' instead of this RID.");

        var roleRoot = $"payload/app/{rid}/";
        var libraryRoot = roleRoot + entryPoint[..(entryPoint.LastIndexOf('/') + 1)];
        var entries = projectedEntries.ToHashSet(StringComparer.Ordinal);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in deps.GetProperty("targets").GetProperty(activeTarget).EnumerateObject())
        {
            if (!library.Value.TryGetProperty("native", out var native))
                continue;
            foreach (var asset in native.EnumerateObject())
            {
                var name = asset.Name[(asset.Name.LastIndexOf('/') + 1)..];
                if (name == "_._") continue; // NuGet's empty asset marker.
                if (!names.Add(name))
                    throw new InvalidDataException($"{identity}: ambiguous native dependency '{name}'.");

                // Normal .NET component resolution can use a flattened RID build
                // output; the App's fallback also supports exact-RID native paths.
                var flat = libraryRoot + name;
                var nested = libraryRoot + $"runtimes/{rid}/native/" + name;
                var conflicting = entries.Count(path => path[(path.LastIndexOf('/') + 1)..].Equals(name, StringComparison.OrdinalIgnoreCase));
                if (conflicting != 1 || !(entries.Contains(flat) || entries.Contains(nested)))
                    throw new InvalidDataException($"{identity}: expected one exact-RID native asset '{name}' declared by '{library.Name}', found {conflicting} matching name(s) in the projection.");
            }
        }
    }
}
