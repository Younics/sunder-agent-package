using System.Globalization;
using System.Text.Json;
using Sunder.Generated.Packages.Younics_Ai_Agent_Models;
using Sunder.Sdk.Logging;

namespace Sunder.Package.Agent.Builder;

public sealed partial class BuilderToolSource
{
    private const string LogArgumentsSchema = """{"type":"object","properties":{"entryPoint":{"type":"string","maxLength":4096},"since":{"type":"string","format":"date-time"},"until":{"type":"string","format":"date-time"},"minLevel":{"enum":["trace","debug","information","warning","error","critical"]},"source":{"enum":["all","runtime","app"]},"limit":{"type":"integer","minimum":1,"maximum":100,"default":20},"maxBytes":{"type":"integer","minimum":1024,"maximum":32768,"default":16384},"cursor":{"type":"string","maxLength":2048}},"required":["entryPoint"],"additionalProperties":false}""";

    private static bool TryLogArguments(AgentToolRequest request, out Arguments args, out string? error)
    {
        args = new("", null); error = null;
        try
        {
            if (request.ArgumentsJson.Length > 16 * 1024) throw new JsonException();
            using var json = JsonDocument.Parse(request.ArgumentsJson, new JsonDocumentOptions { MaxDepth = 3 });
            var root = json.RootElement;
            string[] allowed = ["entryPoint", "since", "until", "minLevel", "source", "limit", "maxBytes", "cursor"];
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Any(member => !allowed.Contains(member.Name, StringComparer.Ordinal))
                || root.EnumerateObject().GroupBy(member => member.Name, StringComparer.Ordinal).Any(group => group.Count() != 1)
                || !root.TryGetProperty("entryPoint", out var entry) || entry.ValueKind != JsonValueKind.String
                || entry.GetString() is not { Length: > 0 and <= 4096 } path || !IsProjectEntryPoint(path)) throw new JsonException();
            static string? Text(JsonElement root, string name) => root.TryGetProperty(name, out var value)
                ? value.ValueKind == JsonValueKind.String ? value.GetString() : throw new JsonException() : null;
            static DateTimeOffset? Time(JsonElement root, string name)
            {
                var text = Text(root, name);
                if (text is null) return null;
                // Do not reinterpret a model's local wall clock as the host's zone.
                if (text.Length > 64 || !(text.EndsWith('Z') || text.Length >= 6 && text[^3] == ':' && text[^6] is '+' or '-')
                    || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)) throw new JsonException();
                return time;
            }
            static int Number(JsonElement root, string name, int fallback) => root.TryGetProperty(name, out var value)
                ? value.TryGetInt32(out var number) ? number : throw new JsonException() : fallback;
            var level = Text(root, "minLevel") ?? "information";
            var severity = level switch { "trace" => PackageLogLevel.Trace, "debug" => PackageLogLevel.Debug,
                "information" => PackageLogLevel.Information, "warning" => PackageLogLevel.Warning,
                "error" => PackageLogLevel.Error, "critical" => PackageLogLevel.Critical, _ => throw new JsonException() };
            var query = new BuilderLogQuery { Since = Time(root, "since"), Until = Time(root, "until"), MinimumLevel = (int)severity,
                Source = Text(root, "source") ?? "all", MaximumEntries = Number(root, "limit", 20), MaximumBytes = Number(root, "maxBytes", 16384),
                Cursor = Text(root, "cursor") };
            if (query.Since is { } since && query.Until is { } until && since >= until || query.Source is not ("all" or "runtime" or "app")
                || query.MaximumEntries is < 1 or > 100 || query.MaximumBytes is < 1024 or > 32768 || query.Cursor?.Length > 2048)
                throw new JsonException();
            args = new(path, null) { LogQuery = query }; return true;
        }
        catch (Exception failure) when (failure is JsonException or ArgumentException or InvalidOperationException)
        {
            error = "Use an absolute .csproj or sunder.package.json entryPoint; optional since/until need ISO timestamps with offsets. Select minLevel, source (all/runtime/app), limit 1–100 (default 20), maxBytes 1024–32768, and an unchanged query's returned cursor. No extra arguments.";
            return false;
        }
    }

    // Portable relay data, not an App-only SDK service/type in the Worker role.
    private sealed record BuilderLogQuery
    {
        public DateTimeOffset? Since { get; init; }
        public DateTimeOffset? Until { get; init; }
        public int MinimumLevel { get; init; }
        public required string Source { get; init; }
        public int MaximumEntries { get; init; }
        public int MaximumBytes { get; init; }
        public string? Cursor { get; init; }
    }
}
