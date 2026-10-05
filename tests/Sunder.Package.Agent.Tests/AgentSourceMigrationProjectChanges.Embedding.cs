using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeAgentEmbeddingBindings(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Runtime/Rpc/AgentRpcCatalog.cs", StringComparison.Ordinal))
        {
            const string addition = """

                    private Func<ISunderRpcClient, SunderRpcProviderSnapshot, TClient>? ProviderFactory { get; init; }

                    internal static AgentRpcService<TClient> WithProviderIdentity(
                        string contractId, Func<ISunderRpcClient, SunderRpcProviderSnapshot, TClient> factory)
                        => new(contractId, static (_, _) => throw new InvalidOperationException("The binding requires an exact provider snapshot."))
                        { ProviderFactory = factory };

                    internal TClient CreateClient(ISunderRpcClient client, SunderRpcProviderSnapshot provider)
                        => ProviderFactory is { } factory ? factory(client, provider) : CreateClient(client, provider.Endpoint);
                """;
            Assert.Equal(2, text.Split(addition + "\n", StringSplitOptions.None).Length);
            text = text.Replace(addition + "\n", "", StringComparison.Ordinal);
            const string factory = "_clientBinding = service.CreateClient(client, provider);";
            Assert.Equal(2, text.Split(factory, StringSplitOptions.None).Length);
            return text.Replace(factory, "_clientBinding = service.CreateClient(client, provider.Endpoint);", StringComparison.Ordinal);
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent/HistorySearch/HistoryEmbeddingProviderCatalog.cs", StringComparison.Ordinal))
        {
            const string import = "using Sunder.Package.Agent.Runtime.Rpc;\n";
            Assert.Equal(2, text.Split(import, StringSplitOptions.None).Length);
            text = text.Replace(import, "", StringComparison.Ordinal);
            Assert.Equal(2, text.Split("AgentEmbeddingBindings.Service", StringSplitOptions.None).Length);
            return text.Replace("AgentEmbeddingBindings.Service", "AgentRpcServices.EmbeddingProviders", StringComparison.Ordinal);
        }
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentProfileService.cs", StringComparison.Ordinal)
            || normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentProfileStackContributor.cs", StringComparison.Ordinal))
        {
            const string binding = "Sunder.Package.Agent.Runtime.Rpc.AgentEmbeddingBindings.Service";
            Assert.Equal(2, text.Split(binding, StringSplitOptions.None).Length);
            text = text.Replace(binding, "AgentRpcServices.EmbeddingProviders", StringComparison.Ordinal);
        }
        return text;
    }
}
