using Sunder.Sdk.Configuration;

namespace Sunder.Package.Agent.Memory.Semantic;

public static class MemorySemanticPackageConfiguration
{
    public static PackageConfigurationSchema Schema { get; } = new(
        "sunder.package.agent.memory.semantic",
        "Sunder Agent Memory Semantic",
        "Configure semantic indexing and retrieval behavior for the optional memory package.",
        [
            new PackageConfigurationSection(
                "semantic",
                "Semantic Retrieval",
                "Controls optional embedding-based indexing and retrieval. Memory still works without semantic retrieval when disabled or unconfigured.",
                [
                    new PackageConfigurationField(
                        "semantic.enabled",
                        "Enable semantic retrieval",
                        PackageConfigurationFieldKind.Boolean,
                        Description: "When enabled, the memory package will try to create and use embeddings for semantic recall when the profile has an embedding provider/model configured.",
                        DefaultValue: "true"),
                    new PackageConfigurationField(
                        "semantic.batchSize",
                        "Embedding batch size",
                        PackageConfigurationFieldKind.Text,
                        Description: "Maximum number of memory items to embed in one batch request when the selected embedding provider supports batching.",
                        DefaultValue: "16",
                        Placeholder: "16"),
                    new PackageConfigurationField(
                        "semantic.maxCanonicalTextChars",
                        "Max canonical text chars",
                        PackageConfigurationFieldKind.Text,
                        Description: "Maximum number of characters from the canonical memory text sent for embedding.",
                        DefaultValue: "1200",
                        Placeholder: "1200"),
                    new PackageConfigurationField(
                        "semantic.reindex.mode",
                        "Stale reindex mode",
                        PackageConfigurationFieldKind.Select,
                        Description: "Choose whether stale or missing embeddings should be regenerated lazily during recall.",
                        DefaultValue: "lazy",
                        Options:
                        [
                            new PackageConfigurationOption("lazy", "Lazy on recall"),
                            new PackageConfigurationOption("never", "Never regenerate automatically")
                        ])
                ])
        ]);
}
