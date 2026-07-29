using Sunder.Sdk.Settings;

namespace Sunder.Package.Agent.Memory.Semantic;

public static class MemorySemanticPackageConfiguration
{
    public static PackageSettingsSchema Schema { get; } = new(
        "Configure semantic indexing and retrieval behavior for the optional memory package.",
        [
            new PackageSettingsSection(
                "semantic",
                "Semantic Retrieval",
                "Controls optional embedding-based indexing and retrieval. Memory still works without semantic retrieval when disabled or unconfigured.",
                [
                    new PackageSettingsField(
                        "semantic.enabled",
                        "Enable semantic retrieval",
                        PackageSettingsFieldKind.Boolean,
                        description: "When enabled, the memory package will try to create and use embeddings for semantic recall when the profile has an embedding provider/model configured.",
                        defaultValue: "true"),
                    new PackageSettingsField(
                        "semantic.batchSize",
                        "Embedding batch size",
                        PackageSettingsFieldKind.Text,
                        description: "Maximum number of memory items to embed in one batch request when the selected embedding provider supports batching.",
                        defaultValue: "16",
                        placeholder: "16"),
                    new PackageSettingsField(
                        "semantic.maxCanonicalTextChars",
                        "Max canonical text chars",
                        PackageSettingsFieldKind.Text,
                        description: "Maximum number of characters from the canonical memory text sent for embedding.",
                        defaultValue: "1200",
                        placeholder: "1200"),
                    new PackageSettingsField(
                        "semantic.reindex.mode",
                        "Stale reindex mode",
                        PackageSettingsFieldKind.Select,
                        description: "Choose whether stale or missing embeddings are regenerated during recall, continuously in the background, or only by explicit reindexing.",
                        defaultValue: "lazy",
                        options:
                        [
                            new PackageSettingsOption("lazy", "Lazy on recall"),
                            new PackageSettingsOption("eager", "Eager background indexing"),
                            new PackageSettingsOption("never", "Never regenerate automatically")
                        ])
                ])
        ]);
}
