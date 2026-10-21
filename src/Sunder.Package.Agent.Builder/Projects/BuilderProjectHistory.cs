using System.Text.Json;

namespace Sunder.Package.Agent.Builder;

internal static class BuilderProjectHistory
{
    internal static string? IdEditReason(BuilderProjectSource source, string? requiredReceiptOwner)
    {
        foreach (var file in source.Files)
        {
            var name = Path.GetFileName(file.Key);
            if (name == "sunder-contracts.owned.json" || name.Contains("identity-fork", StringComparison.OrdinalIgnoreCase) || name.Contains("publication", StringComparison.OrdinalIgnoreCase))
                return $"Retained owned baseline/history '{file.Key}' prevents automatic ID editing. Its authority and history must remain intact.";
            if (name == "owned-baseline.sha256" && file.Value.Text != "absent\n")
                return $"Prior public baseline evidence '{file.Key}' requires restoring the original baseline; absence is not permission to change identity.";
            if (name is not ("authoring-receipt.v1.json" or "app-authoring-receipt.v1.json" or "package-export.v1.json")) continue;
            try
            {
                using var document = BuilderProjectSource.Json(file.Value.Bytes);
                var root = document.RootElement;
                var owner = root.GetProperty("packageId").GetString();
                if (root.GetProperty("schemaVersion").GetInt32() != 1 || owner is null || requiredReceiptOwner is not null && owner != requiredReceiptOwner) return Unknown(file.Key);
                BuilderProjectSource.ValidateId(owner);
                if (!Hash(root.GetProperty("inputFingerprint").GetString()) || !Hash(root.GetProperty("lockSha256").GetString())
                    || root.GetProperty("producer").GetProperty("name").GetString() is not ("Sunder.Package.Build" or "@sunder/package-tool")) return Unknown(file.Key);
                var version = root.GetProperty("packageVersion").GetString();
                if (string.IsNullOrEmpty(version)) return Unknown(file.Key);
                var contracts = root.GetProperty("contracts").EnumerateArray().ToArray();
                if (contracts.Length > 128) return Unknown(file.Key);
                var export = name == "package-export.v1.json";
                var bundles = export ? [] : root.GetProperty("bundles").EnumerateArray().ToArray();
                if (!export && bundles.Length != contracts.Length) return Unknown(file.Key);
                foreach (var contract in contracts)
                {
                    var descriptor = BuilderProjectSource.Utf8.GetBytes(contract.GetProperty("canonicalDescriptor").GetString()!);
                    if (contract.GetProperty("descriptorBytes").GetInt32() != descriptor.Length || contract.GetProperty("descriptorSha256").GetString() != BuilderProjectSource.Hash(descriptor)
                        || !Hash(contract.GetProperty("abiSha256").GetString()) || contract.GetProperty("modelProjection").ValueKind != JsonValueKind.Object) return Unknown(file.Key);
                    var owned = export ? contract.GetProperty("source").GetString() : bundles.Single(bundle => bundle.GetProperty("contractKey").GetString() == contract.GetProperty("contractKey").GetString()).GetProperty("source").GetString();
                    if (owned is not ("owned" or "imported")) return Unknown(file.Key);
                    if (owned == "owned" && (contract.GetProperty("exposure").GetString() != "package" || contract.GetProperty("contractVersion").GetString() != version))
                        return $"Public or historical owned contracts in '{file.Key}' prevent automatic ID editing. Restore and preserve the original owned baseline.";
                }
            }
            catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
            { return Unknown(file.Key); }
        }
        foreach (var file in source.Files.Where(file => file.Key.EndsWith(".sunder-generated-output", StringComparison.Ordinal)))
        {
            var directory = file.Key[..^".sunder-generated-output".Length];
            if (source.Files.Keys.Any(path => path == directory + "/authoring-receipt.v1.json" || path == directory + "/package-export.v1.json")) continue;
            // Design-time-only output is not publication evidence, but must explicitly say so.
            if (source.Files.TryGetValue(directory + "/design-time-only.json", out var editor))
            {
                try
                {
                    using var document = BuilderProjectSource.Json(editor.Bytes);
                    if (document.RootElement.GetProperty("purpose").GetString() == "editor-imports-only" && document.RootElement.GetProperty("artifactEligible").ValueKind == JsonValueKind.False) continue;
                }
                catch (Exception error) when (error is JsonException or InvalidDataException or InvalidOperationException or KeyNotFoundException) { }
            }
            // Other build outputs are preserved; only RPC binding ownership markers claim baseline history.
            if (directory.EndsWith("/rpc", StringComparison.Ordinal) || directory.EndsWith("/sunder-rpc", StringComparison.Ordinal)) return Unknown(file.Key);
        }
        return null;
    }
    private static bool Hash(string? value) => value is { Length: 64 } && value.All(character => character is >= 'a' and <= 'f' or >= '0' and <= '9');
    private static string Unknown(string path) => $"Unproven or foreign generated evidence '{path}' prevents automatic ID editing. Restore the original source/baseline rather than discarding history.";
}
