using System.Text.Json;
using System.Text.Json.Nodes;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderProjectToolchainUpgrade
{
    // Discovery shares the guarded edit plan with the explicit upgrade. It never
    // creates a transaction, selects tools, writes files or executes project code.
    internal static BuilderProjectToolchainAssessment Assess(BuilderProjectSource source, BuilderProjectKind kind,
        Sunder.Sdk.Abstractions.SunderDevelopmentToolchain versions)
    {
        var version = kind == BuilderProjectKind.Dotnet ? versions.DotnetSdkVersion : versions.NodeVersion;
        try
        {
            var requirements = BuilderToolchainRequirements.Read(kind, source.Root);
            if (requirements.Allows(kind, version, versions.NpmVersion)) return new(false, false, null);
        }
        catch (Exception error) when (IsAssessmentFailure(error))
        {
            return new(false, false, "Toolchain requirements could not be checked. Review the project configuration. " + error.Message);
        }
        try
        {
            _ = PlanEdits(source, kind, version, versions.NpmVersion);
            return new(true, true, $"Project development pins exclude Sunder's current {(kind == BuilderProjectKind.Dotnet ? ".NET SDK" : "Node.js")} {version}"
                + (kind == BuilderProjectKind.Node ? $" / npm {versions.NpmVersion}" : string.Empty)
                + ". Upgrade them explicitly to use this toolchain. Target frameworks and package dependencies will not change.");
        }
        catch (Exception error) when (IsAssessmentFailure(error))
        {
            return new(true, false, "Project requirements exclude Sunder's current toolchain. " + error.Message);
        }
    }

    private static bool IsAssessmentFailure(Exception error)
        => error is IOException or InvalidDataException or InvalidOperationException or ArgumentException or JsonException or UnauthorizedAccessException;

    internal static void ValidatePinChange(string path, byte[] before, byte[] after)
    {
        if (path == ".tool-versions")
        {
            static string OtherTools(byte[] bytes) => string.Join('\n', new BuilderSourceFile(".tool-versions", bytes).Text.Split('\n')
                .Where(line => line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() != "nodejs"));
            if (OtherTools(before) != OtherTools(after))
                throw new InvalidDataException("Toolchain recovery cannot change unrelated tools in .tool-versions.");
            return;
        }
        if (path is not ("global.json" or "package.json")) return;
        JsonObject Normalize(byte[] bytes)
        {
            var json = JsonNode.Parse(new BuilderSourceFile(path, bytes).Text,
                documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!.AsObject();
            if (path == "global.json")
            {
                var sdk = json["sdk"] as JsonObject ?? new JsonObject();
                foreach (var key in new[] { "version", "rollForward", "allowPrerelease" }) sdk.Remove(key);
                json["sdk"] = sdk;
            }
            else
            {
                json.Remove("packageManager");
                if (json["volta"] is JsonObject volta) { volta.Remove("node"); volta.Remove("npm"); }
                if (json["devEngines"] is JsonObject engines)
                    foreach (var key in new[] { "runtime", "packageManager" })
                        if (engines[key] is JsonObject engine) engine.Remove("version");
            }
            return json;
        }
        if (!JsonNode.DeepEquals(Normalize(before), Normalize(after)))
            throw new InvalidDataException("Toolchain recovery cannot change package dependencies, runtime requirements or unrelated SDK settings.");
    }

    internal static async Task<BuilderProjectInspection> UpgradeAsync(BuilderProjectInspection expected,
        BuilderToolchainInstallation tools, CancellationToken token)
    {
        var root = expected.ProjectFolder;
        await using var lease = await BuilderMetadataTransaction.LeaseAsync(root, token).ConfigureAwait(false);
        var source = await BuilderProjectSource.CaptureAsync(root, token).ConfigureAwait(false);
        if (source.Revision != expected.SourceRevision) throw new BuilderProjectConflictException("Source changed before the toolchain upgrade. Inspect it again.");
        var edits = PlanEdits(source, tools.Kind, tools.Version, tools.NpmVersion);
        if (edits.Count > 0) await BuilderMetadataTransaction.CommitAsync(root, Path.GetFileName(expected.EntryPoint), source, edits, token).ConfigureAwait(false);
        return await new BuilderProjectDiscovery().InspectAsync(expected.EntryPoint, token).ConfigureAwait(false);
    }

    private static Dictionary<string, byte[]> PlanEdits(BuilderProjectSource source, BuilderProjectKind kind, string version, string? npmVersion)
    {
        var root = source.Root;
        if (source.UnsafeReason is not null) throw new InvalidOperationException(source.UnsafeReason);
        var names = kind == BuilderProjectKind.Dotnet ? new[] { "global.json" } : [".node-version", ".nvmrc", ".tool-versions"];
        foreach (var name in names)
        {
            if (source.Files.TryGetValue(name, out var local)
                && (name != ".tool-versions" || NodePins(local.Text).Length > 0)) continue;
            for (var parent = Directory.GetParent(root); parent is not null; parent = parent.Parent)
                if (File.Exists(Path.Combine(parent.FullName, name)))
                {
                    if (!BuilderToolchainRequirements.Read(kind, parent.FullName).Allows(kind, version, npmVersion))
                        throw new InvalidOperationException($"Update the inherited toolchain pin manually: {Path.Combine(parent.FullName, name)}. Package Maker will not rewrite a shared parent configuration.");
                    break;
                }
        }
        var edits = new Dictionary<string, byte[]>();
        foreach (var name in names.Where(source.Files.ContainsKey))
        {
            var file = source.Files[name];
            if (name == "global.json")
            {
                var json = JsonNode.Parse(file.Text, documentOptions: new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip })!.AsObject();
                var sdk = json["sdk"] as JsonObject ?? new JsonObject();
                sdk["version"] = version; sdk["rollForward"] = "disable"; sdk["allowPrerelease"] = false;
                json["sdk"] = sdk;
                edits[name] = file.Encode(json.ToJsonString(new() { WriteIndented = true }) + "\n");
            }
            else if (name == ".tool-versions")
            {
                var lines = file.Text.Split('\n');
                var pins = NodePins(file.Text);
                if (pins.Length > 1) throw new InvalidDataException("Resolve duplicate Node pins in .tool-versions before upgrading.");
                if (pins.Length == 1) { lines[pins[0].index] = "nodejs " + version; edits[name] = file.Encode(string.Join('\n', lines)); }
            }
            else edits[name] = file.Encode(version + "\n");
        }
        if (kind == BuilderProjectKind.Node && source.Files.TryGetValue("package.json", out var package))
        {
            var json = JsonNode.Parse(package.Text)!.AsObject();
            if (json["packageManager"] is JsonValue manager && !manager.GetValue<string>().StartsWith("npm@", StringComparison.Ordinal))
                throw new InvalidOperationException("This project selects another package manager; Package Maker will not replace it with npm.");
            json["packageManager"] = "npm@" + npmVersion;
            if (json["volta"] is JsonObject volta)
            {
                if (volta.ContainsKey("extends")) throw new InvalidOperationException("Resolve inherited Volta pins manually before upgrading.");
                volta["node"] = version; volta["npm"] = npmVersion;
            }
            if (json["devEngines"] is JsonObject engines)
                foreach (var (key, pin) in new[] { ("runtime", version), ("packageManager", npmVersion) })
                    if (engines[key] is { } requirement)
                    {
                        if (requirement is not JsonObject engine
                            || engine["name"] is { } engineName && engineName.GetValue<string>() != (key == "runtime" ? "node" : "npm"))
                            throw new InvalidDataException($"Resolve unsupported devEngines.{key} requirements manually before upgrading.");
                        engine["version"] = pin;
                    }
            edits["package.json"] = package.Encode(json.ToJsonString(new() { WriteIndented = true }) + "\n");
        }
        foreach (var (path, bytes) in edits) ValidatePinChange(path, source.Files[path].Bytes, bytes);
        if (!BuilderToolchainRequirements.Read(kind, root, proposedFiles: edits).Allows(kind, version, npmVersion))
            throw new InvalidOperationException("Review inherited pins and runtime engine requirements manually. Package Maker will not rewrite shared parent configuration or runtime requirements.");
        return edits;
    }

    private static (string[] Parts, int index)[] NodePins(string text)
        => text.Split('\n').Select((line, index) => (Parts: line.Split('#')[0].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries), index))
            .Where(line => line.Parts.Length > 0 && line.Parts[0] == "nodejs").ToArray();
}
