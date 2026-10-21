using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderNodeMetadata
{
    internal static BuilderSourceMetadata Inspect(BuilderProjectSource source)
    {
        using var document = BuilderProjectSource.Json(source.Files["sunder.package.json"].Bytes);
        var config = document.RootElement;
        if (config.ValueKind != JsonValueKind.Object || !config.TryGetProperty("schemaVersion", out var version) || !version.TryGetInt32(out var schema) || schema != 3)
            throw new NotSunderProjectException("Node projects require a strict schemaVersion 3 sunder.package.json.");
        Only(config, "schemaVersion", "id", "name", "summary", "version", "entry", "services", "workerCapabilities", "dependencies", "outputDirectory", "maximumPackageBytes", "app", "nativeAssets");
        var id = Text(config, "id"); var name = Text(config, "name");
        BuilderProjectSource.ValidateId(id); BuilderProjectSource.ValidateName(name);
        var semver = Text(config, "version");
        if (semver.Length > 256 || !Regex.IsMatch(semver, @"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(?:-(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*)(?:\.(?:0|[1-9][0-9]*|[0-9]*[a-zA-Z-][0-9a-zA-Z-]*))*)?(?:\+[0-9a-zA-Z-]+(?:\.[0-9a-zA-Z-]+)*)?$", RegexOptions.CultureInvariant))
            throw new InvalidDataException("Node package version must be strict full SemVer.");
        string? idReason = null;
        if (config.TryGetProperty("services", out var services))
        {
            Only(services, "module"); var module = RelativePath(Text(services, "module"));
            if (!module.EndsWith(".ts", StringComparison.Ordinal) && !module.EndsWith(".js", StringComparison.Ordinal) || module.EndsWith(".d.ts", StringComparison.Ordinal) || config.TryGetProperty("entry", out _))
                throw new InvalidDataException("Schema 3 services require an authored .ts/.js module and no entry property.");
            if (module is not ("src/services.ts" or "src/services.js") || !source.Files.TryGetValue(module, out var file))
                idReason = "Automatic Node ID editing requires the local src/services.ts (or .js) private starter module.";
            else idReason = PrivateStarterReason(file.Text);
        }
        else { RelativePath(Text(config, "entry")); idReason = "Automatic ID editing requires inferred private services; custom entry-point projects are name-only."; }
        if (config.TryGetProperty("workerCapabilities", out var capabilities))
        {
            if (capabilities.ValueKind != JsonValueKind.Array || capabilities.GetArrayLength() > 32) throw new InvalidDataException("workerCapabilities must be a bounded array.");
            var known = new HashSet<string>(["auth.v1", "callbacks.v1", "logging.v1", "role-local-workspace.v1", "rpc.extended-deadline.v1", "secrets.v1", "settings.schema.v1", "settings.v1", "storage.key-migration.v1", "storage.v1"], StringComparer.Ordinal);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in capabilities.EnumerateArray()) if (item.ValueKind != JsonValueKind.String || !known.Contains(item.GetString()!) || !seen.Add(item.GetString()!)) throw new InvalidDataException("Unsupported or duplicate worker capability.");
        }
        else throw new InvalidDataException("workerCapabilities must be an explicit array.");
        if (config.TryGetProperty("outputDirectory", out var output)) RelativePath(output.GetString()!, source: false);
        if (config.TryGetProperty("maximumPackageBytes", out var maximum) && (!maximum.TryGetInt64(out var limit) || limit is < 1 or > 536870912)) throw new InvalidDataException("maximumPackageBytes must be a positive integer at most 536870912.");
        if (config.TryGetProperty("summary", out var summary) && (summary.ValueKind != JsonValueKind.String || summary.GetString()!.Length is < 1 or > 2048)) throw new InvalidDataException("summary must be a nonempty string of at most 2048 characters.");
        if (config.TryGetProperty("dependencies", out var dependencies))
        {
            if (dependencies.ValueKind != JsonValueKind.Array || dependencies.GetArrayLength() > 128) throw new InvalidDataException("dependencies must be a bounded array.");
            foreach (var dependency in dependencies.EnumerateArray()) { Only(dependency, "packageId", "versionRange"); BuilderProjectSource.ValidateId(Text(dependency, "packageId")); _ = Text(dependency, "versionRange"); }
        }
        if (config.TryGetProperty("app", out var app))
        {
            Only(app, "root", "entryPoint", "views");
            var appRoot = app.TryGetProperty("root", out var configuredRoot) ? configuredRoot.GetString()! : ".";
            if (appRoot != ".") RelativePath(appRoot);
            var appEntry = app.TryGetProperty("entryPoint", out var configuredEntry) ? configuredEntry.GetString()! : "index.html";
            RelativePath(appEntry);
            if (!appEntry.EndsWith(".html", StringComparison.Ordinal)) throw new InvalidDataException("app.entryPoint must be an HTML file.");
            if (appRoot != "app") idReason ??= "Automatic ID editing requires the standard app/ React starter root.";
            var views = app.GetProperty("views");
            if (views.ValueKind != JsonValueKind.Array || views.GetArrayLength() is < 1 or > 32) throw new InvalidDataException("app.views must contain 1..32 entries.");
            var viewIds = new HashSet<string>(StringComparer.Ordinal);
            var routes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var view in views.EnumerateArray())
            {
                Only(view, "viewId", "displayName", "route", "defaultPlacement", "showInHotbar", "icon");
                var viewId = Text(view, "viewId"); BuilderProjectSource.ValidateId(viewId); BuilderProjectSource.ValidateName(Text(view, "displayName"));
                if (!viewId.StartsWith(id + ".", StringComparison.Ordinal) || Text(view, "displayName").Length > 128) throw new InvalidDataException("View IDs must belong to the package; view names are at most 128 characters.");
                if (!viewIds.Add(viewId)) throw new InvalidDataException("Duplicate view ID.");
                if (viewId != id + ".main") idReason ??= "Automatic ID editing supports only the packageId.main starter view; custom view identities require manual review.";
                var route = Text(view, "route");
                if (route.Length > 200 || !route.StartsWith('/') || route.Contains("//", StringComparison.Ordinal) || route.Split('/').Any(part => part is "." or "..") || !routes.Add(route)
                    || Text(view, "defaultPlacement") is not ("leftTop" or "middle" or "rightTop" or "leftBottom" or "rightBottom")) throw new InvalidDataException("Invalid app view route or placement.");
                if (!view.TryGetProperty("showInHotbar", out var hotbar) || hotbar.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("showInHotbar must be boolean.");
            }
        }
        if (config.TryGetProperty("nativeAssets", out var native))
        {
            Only(native, "win-x64", "win-arm64", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64");
            var count = 0;
            foreach (var rid in native.EnumerateObject())
            {
                if (rid.Value.ValueKind != JsonValueKind.Array || rid.Value.GetArrayLength() == 0 || (count += rid.Value.GetArrayLength()) > 256) throw new InvalidDataException("Invalid bounded nativeAssets array.");
                foreach (var asset in rid.Value.EnumerateArray()) { Only(asset, "sourcePath", "targetPath"); RelativePath(Text(asset, "sourcePath")); RelativePath(Text(asset, "targetPath"), source: false); }
            }
            if (count == 0) throw new InvalidDataException("nativeAssets requires at least one asset.");
            idReason ??= "Native-asset identity handling is outside the starter metadata editor.";
        }
        // A foreign import lock is never rewritten. Imports need compiler-qualified review, not a guessed string edit.
        if (source.Files.TryGetValue("sunder-contracts.lock.json", out var lockFile))
        {
            using var lockDocument = BuilderProjectSource.Json(lockFile.Bytes);
            foreach (var field in lockDocument.RootElement.EnumerateObject().Where(property => property.Name != "lockVersion"))
                if (field.Value.ValueKind != JsonValueKind.Object || field.Value.EnumerateObject().Any()) idReason ??= "Nonempty or unrecognized API imports require manual ID review. The foreign import lock is preserved byte-for-byte.";
        }
        else idReason ??= "The canonical sunder-contracts.lock.json is missing. Restore the starter import lock before editing its ID.";
        foreach (var file in source.Files.Where(file => file.Key.EndsWith(".ts", StringComparison.Ordinal) || file.Key.EndsWith(".tsx", StringComparison.Ordinal) || file.Key.EndsWith(".js", StringComparison.Ordinal) || file.Key.EndsWith(".jsx", StringComparison.Ordinal)))
        {
            List<Token> tokens;
            try { tokens = Tokens(file.Key.EndsWith(".tsx", StringComparison.Ordinal) || file.Key.EndsWith(".jsx", StringComparison.Ordinal) ? StarterHeader(file.Value.Text) : file.Value.Text); }
            catch (InvalidDataException) { idReason ??= $"Source syntax in '{file.Key}' is outside the bounded starter identity reader."; continue; }
            for (var index = 0; index < tokens.Count; index++)
            {
                var literal = tokens[index];
                var identity = literal.StringValue;
                if (identity is null && literal.Value.Length > 2 && literal.Value[0] is '\'' or '`') identity = literal.Value[1..^1];
                if (identity is null || identity != id && !identity.StartsWith(id + ".", StringComparison.Ordinal)) continue;
                if (file.Key is "app/src/App.tsx" or "app/src/App.jsx" && index >= 3 && tokens[index - 3].Value == "const" && tokens[index - 2].Value == "packageId" && tokens[index - 1].Value == "=" && identity == id) continue;
                idReason ??= $"Unrecognized package identity literal in '{file.Key}'. Custom identities are outside the starter editor.";
            }
        }
        return new(name, id, IdReadOnlyReason: idReason);
    }

    internal static Dictionary<string, byte[]> Plan(BuilderProjectSource source, BuilderProjectInspection expected, string name, string id)
    {
        var changed = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var configFile = source.Files["sunder.package.json"];
        var config = JsonNode.Parse(configFile.Bytes)!.AsObject();
        config["id"] = id; config["name"] = name;
        if (config["app"]?["views"] is JsonArray views)
            foreach (var item in views)
            {
                var view = item!.AsObject();
                if (id != expected.PackageId && view["viewId"]!.GetValue<string>() == expected.PackageId + ".main")
                { BuilderProjectSource.ValidateId(id + ".main"); view["viewId"] = id + ".main"; }
                if (view["displayName"]!.GetValue<string>() == expected.DisplayName)
                {
                    if (name.Length > 128) throw new ArgumentException("The mirrored Node view display name must be at most 128 characters.", nameof(name));
                    view["displayName"] = name;
                }
            }
        if (id != expected.PackageId) { BuilderProjectSource.ValidateId(id + ".messages"); BuilderProjectSource.ValidateId(id + ".messages.provider"); }
        if (id != expected.PackageId || name != expected.DisplayName)
            changed["sunder.package.json"] = configFile.Encode(config.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + (configFile.Text.EndsWith('\n') ? "\n" : ""));
        foreach (var path in new[] { "src/services.ts", "src/services.js", "app/src/App.tsx", "app/src/App.jsx" })
        {
            if (!source.Files.TryGetValue(path, out var file)) continue;
            var tokens = Tokens(StarterHeader(file.Text)); var edits = new List<BuilderTextEdit>();
            var depth = 0;
            for (var index = 0; index + 4 < tokens.Count; index++)
            {
                var current = tokens[index];
                if (current.Value == "{") depth++; else if (current.Value == "}") depth--;
                if (depth != 0 || current.Value != "const" || tokens[index + 2].Value != "=" || tokens[index + 4].Value != ";") continue;
                var key = tokens[index + 1].Value; var literal = tokens[index + 3];
                var old = key == "packageName" ? expected.DisplayName : key == "packageId" && path.StartsWith("app/", StringComparison.Ordinal) ? expected.PackageId : null;
                var next = key == "packageName" ? name : id;
                if (old is not null && literal.StringValue == old && old != next) edits.Add(new(literal.Start, literal.Length, JsonSerializer.Serialize(next)));
            }
            if (edits.Count != 0) changed[path] = file.Encode(BuilderTextEdit.Apply(file.Text, edits));
        }
        if (name != expected.DisplayName && source.Files.TryGetValue("app/index.html", out var html))
        {
            var matches = Regex.Matches(html.Text, @"<title>([^<]*)</title>", RegexOptions.CultureInvariant);
            if (matches.Count == 1 && WebUtility.HtmlDecode(matches[0].Groups[1].Value) == expected.DisplayName)
            {
                var value = matches[0].Groups[1];
                changed["app/index.html"] = html.Encode(BuilderTextEdit.Apply(html.Text, [new(value.Index, value.Length, WebUtility.HtmlEncode(name))]));
            }
        }
        return changed;
    }

    private static string? PrivateStarterReason(string text)
    {
        const string reason = "Automatic Node ID editing supports the private messages defineService starter with inline methods/constraints options, no custom identity/exposure, and no extra imports/exports. Computed or custom service metadata requires manual review.";
        List<Token> tokens;
        try { tokens = Tokens(text); } catch (InvalidDataException) { return reason; }
        if (tokens.Count(token => token.Value == "import") != 1 || tokens.Count(token => token.Value == "export") != 1 || tokens.Count(token => token.Value == "defineService") != 2
            || tokens.Any(token => token.Value is "contractId" or "providerId" or "serviceId" or "exposure" or "version" or "...")) return reason;
        var export = tokens.FindIndex(token => token.Value == "export");
        if (export < 0 || string.Join(" ", tokens.Skip(export).Take(6).Select(token => token.Value)) != "export const messages = defineService (") return reason;
        var importEnd = tokens.FindIndex(token => token.Value == ";");
        var import = string.Join(" ", tokens.Take(importEnd + 1).Select(token => token.Value));
        if (import is not ("import { defineService , type ServiceContext } from \"@sunder/sdk\" ;" or "import { defineService } from \"@sunder/sdk\" ;")) return reason;
        var header = tokens.Skip(importEnd + 1).Take(export - importEnd - 1).ToArray();
        if (header.Length != 5 || header[0].Value != "const" || header[1].Value != "packageName" || header[2].Value != "=" || header[3].StringValue is null || header[4].Value != ";") return reason;
        var sdk = tokens.FirstOrDefault(token => token.StringValue == "@sunder/sdk");
        if (sdk is null) return reason;
        var call = export + 6;
        if (tokens[call].Value != "{") return reason;
        var depth = 0;
        for (var index = call; index < tokens.Count; index++)
        {
            if (tokens[index].Value is "{" or "(" or "[") depth++;
            else if (tokens[index].Value is "}" or ")" or "]") depth--;
            if (depth == 0)
            {
                var rest = tokens.Skip(index + 1).Select(token => token.Value).ToArray();
                if (rest.Length == 2 && rest[0] == ")" && rest[1] == ";") return null;
                if (rest.Length < 5 || rest[0] != "," || rest[1] != "{" || rest[^2] != ")" || rest[^1] != ";") return reason;
                var level = 0;
                for (var option = 1; option < rest.Length - 2; option++)
                {
                    if (rest[option] is "{" or "[" or "(") level++;
                    else if (rest[option] is "}" or "]" or ")") level--;
                    else if (level == 1 && rest[option] != "," && rest[option] != ":" && rest[option] is not ("methods" or "constraints")) return reason;
                }
                return null;
            }
        }
        return reason;
    }

    private static string StarterHeader(string text)
    {
        // Fixed starter labels precede the exported service/component. JSX body text is not JavaScript string syntax.
        var export = Regex.Match(text, @"(?m)^[ \t]*export\b", RegexOptions.CultureInvariant);
        return export.Success ? text[..export.Index] : text;
    }

    private sealed record Token(string Value, string? StringValue, int Start, int Length);
    // A bounded lexical reader only for recognizing fixed starter declarations. Full service qualification on generated projects is done by package-tool's TS compiler.
    private static List<Token> Tokens(string text)
    {
        var result = new List<Token>();
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            if (text.AsSpan(i).StartsWith("//")) { while (i < text.Length && text[i] != '\n') i++; continue; }
            if (text.AsSpan(i).StartsWith("/*")) { var end = text.IndexOf("*/", i + 2, StringComparison.Ordinal); if (end < 0) throw new InvalidDataException("Unclosed source comment."); i = end + 2; continue; }
            var start = i; string? value = null;
            if (text[i] is '"' or '\'' or '`')
            {
                var quote = text[i++];
                while (i < text.Length && text[i] != quote) { if (text[i] == '\\') i++; i++; }
                if (i >= text.Length) throw new InvalidDataException("Unclosed source literal.");
                i++;
                if (quote == '"') { try { value = JsonSerializer.Deserialize<string>(text[start..i]); } catch (JsonException) { } }
            }
            else if (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$') { while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '_' or '$')) i++; }
            else if (text.AsSpan(i).StartsWith("...")) i += 3;
            else i++;
            result.Add(new(text[start..i], value, start, i - start));
        }
        return result;
    }
    private static void Only(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object || value.EnumerateObject().Any(property => !names.Contains(property.Name, StringComparer.Ordinal))) throw new InvalidDataException("Node schema 3 config contains unsupported properties.");
    }
    private static string Text(JsonElement value, string key) => value.TryGetProperty(key, out var property) && property.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(property.GetString())
        ? property.GetString()! : throw new InvalidDataException($"Node config requires '{key}'.");
    private static string RelativePath(string path, bool source = true)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 240 || path.Contains('\\') || path.Contains(':') || path.StartsWith('/') || path.Split('/').Any(segment => segment is "" or "." or ".." || segment is ".git" or "node_modules" or ".sunder" || source && BuilderProjectSource.OutputFolders.Contains(segment)))
            throw new InvalidDataException("Node authored paths must be regular relative source paths outside output/dependency directories.");
        return path;
    }
}
