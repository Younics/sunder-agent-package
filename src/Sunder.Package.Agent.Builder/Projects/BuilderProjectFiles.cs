using System.Text.Json;
using Sunder.Sdk.Packaging;

namespace Sunder.Package.Agent.Builder;

/// <summary>Portable, read-only path/JSON guards shared by source discovery and built-output validation.</summary>
[SunderPackageCodeRole(SunderPackageCodeRole.Shared)]
internal static class BuilderProjectFiles
{
    internal static void EnsureSafePath(string root, string path)
    {
        root = Path.GetFullPath(root); path = Path.GetFullPath(path);
        var relative = Path.GetRelativePath(root, path);
        if (relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Project paths must remain inside the selected folder.");
        for (var current = path; !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
        {
            if (OperatingSystem.IsMacOS() && current is "/var" or "/tmp") break; // OS-owned aliases.
            if (!File.Exists(current) && !Directory.Exists(current))
            {
                if (new FileInfo(current).LinkTarget is not null) throw new InvalidDataException($"Dangling symbolic link is not a project source: {current}");
                continue;
            }
            if ((File.GetAttributes(current) & (FileAttributes.ReparsePoint | FileAttributes.Device)) != 0)
                throw new InvalidDataException($"Symbolic links/reparse points are not supported in project source paths: {current}");
        }
    }

    internal static JsonDocument Json(byte[] bytes)
    {
        var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 64 });
        try { Check(document.RootElement); return document; }
        catch { document.Dispose(); throw; }
        static void Check(JsonElement value)
        {
            if (value.ValueKind == JsonValueKind.Object)
            {
                var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var property in value.EnumerateObject())
                {
                    if (!keys.Add(property.Name)) throw new InvalidDataException("JSON contains duplicate or case-colliding properties.");
                    Check(property.Value);
                }
            }
            else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) Check(item);
        }
    }
}
