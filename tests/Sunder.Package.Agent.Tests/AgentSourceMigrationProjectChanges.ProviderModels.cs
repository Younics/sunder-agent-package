using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal const string ProviderChatModelsSource = "src/Sunder.Package.Agent.Provider.Shared/Models/ProviderChatModels.cs";
    internal const string LegacyProviderCatalogSource = "src/Sunder.Package.Agent.Subagents/Runtime/ProviderModelCatalogAdapter.Legacy.cs";

    internal static string BeforeProviderCatalogSeparation(string path, string text)
    {
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent.Shared/Presentation/ProviderModelLoader.cs", StringComparison.Ordinal)) return text;
        var legacy = ReadMigrationSourceText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, LegacyProviderCatalogSource));
        const string header = "using Sunder.Package.Agent.Contracts.Contracts;\nusing Sunder.Package.Agent.Contracts.Models;\nusing Sunder.Package.Agent.Protocol;\n\nnamespace Sunder.Package.Agent.Shared.Presentation;\n\n#if SUNDER_SUBAGENTS_WORKER\ninternal sealed partial class ProviderModelCatalogAdapter\n{\n";
        const string footer = "}\n#endif\n";
        Assert.StartsWith(header, legacy, StringComparison.Ordinal); Assert.EndsWith(footer, legacy, StringComparison.Ordinal);
        var body = legacy[header.Length..^footer.Length];
        const string record = "    private sealed record OwnedChatProviderReference(";
        Assert.Equal(2, body.Split(record, StringSplitOptions.None).Length);
        var split = body.IndexOf(record, StringComparison.Ordinal);
        text = "using Sunder.Package.Agent.Contracts.Contracts;\n" + text.Replace("using Sunder.Package.Agent.Contracts.Models;\n", "using Sunder.Package.Agent.Contracts.Models;\nusing Sunder.Package.Agent.Protocol;\n", StringComparison.Ordinal);
        text = text.Replace("internal sealed partial class ProviderModelCatalogAdapter(", "internal sealed class ProviderModelCatalogAdapter(", StringComparison.Ordinal);
        const string beforeMethods = "    public static ProviderModelCatalogOption ToCatalogOption(AgentModelDescriptor model)";
        Assert.Equal(2, text.Split(beforeMethods, StringSplitOptions.None).Length);
        text = text.Replace(beforeMethods, body[..split] + beforeMethods, StringComparison.Ordinal);
        const string beforeRecord = "        => new(model.ModelId, model.DisplayName);\n\n}";
        Assert.Equal(2, text.Split(beforeRecord, StringSplitOptions.None).Length);
        return text.Replace(beforeRecord, "        => new(model.ModelId, model.DisplayName);\n\n" + body[split..] + "}", StringComparison.Ordinal);
    }

    internal static string BeforeProviderChatModels(string path, string text)
    {
        if (!path.Replace('\\', '/').EndsWith("/src/Sunder.Package.Agent.Contracts/Rpc/AgentChatProviderRpcBinding.cs", StringComparison.Ordinal)) return text;
        var models = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName, ProviderChatModelsSource))
            .Replace("\r\n", "\n", StringComparison.Ordinal);
        const string header = "using System.Text.Json;\nusing System.Text.Json.Serialization;\nusing Sunder.Package.Agent.Contracts.Models;\nusing Sunder.Sdk.Rpc;\n\nnamespace Sunder.Package.Agent.Protocol;\n\n"
            + "// Package-owned chat values, compiled independently by provider roles. The retained\n"
            + "// Protocol project links this same source while its consumers finish migration.\n";
        Assert.StartsWith(header, models, StringComparison.Ordinal);
        Assert.DoesNotContain("public sealed record ChatCapabilitiesRequest", text, StringComparison.Ordinal);
        return text.TrimEnd() + "\n\n" + models[header.Length..];
    }
}
