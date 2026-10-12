using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static string BeforeMcpRelocation(string path, string text)
    {
        if (!path.Replace('\\', '/').Contains("/src/Sunder.Package.Agent.Mcp/", StringComparison.Ordinal)) return text;
        text = BeforeMcpAppComposition(path, text);
        if (Path.GetFileName(path) == "McpClientConnectionManager.cs")
        {
            const string selected = "    [Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructor]\n"
                + "    internal McpClientConnectionManager(Sunder.Sdk.Abstractions.IPackageContext context, McpOAuthService oauth)\n"
                + "        : this(context.Logging.LoggerFactory, oauth) { }\n\n";
            RequireOnce(selected);
            text = text.Replace(selected, "", StringComparison.Ordinal);
        }
        if (Path.GetFileName(path) == "McpSunderConfigurationSyncService.cs")
        {
            const string lookup = "    private Task<IReadOnlyList<AgentWorkspaceRecord>> ListKnownWorkspacesAsync(CancellationToken cancellationToken)\n"
                + "        => _workspaceCatalog.ListKnownWorkspacesAsync(cancellationToken);";
            var original = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
                "tests/Sunder.Package.Agent.Tests/Fixtures/McpWorkspaceDiscovery.before-source.cs.txt")).Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd();
            var replacements = new (string After, string Before)[]
            {
                ("using Sunder.Package.Agent.Contracts.Models;", "using Sunder.Package.Agent.Contracts.Contracts;\nusing Sunder.Package.Agent.Contracts.Models;"),
                ("using Sunder.Package.Agent.Mcp.Runtime;", "using Sunder.Package.Agent.Protocol;"),
                ("public sealed partial class McpSunderConfigurationSyncService", "public sealed class McpSunderConfigurationSyncService"),
                ("IMcpWorkspaceCatalog workspaceCatalog,", "AgentRpcCatalog? rpcCatalog = null,"),
                ("private readonly IMcpWorkspaceCatalog _workspaceCatalog = workspaceCatalog;", "private readonly AgentRpcCatalog? _rpcCatalog = rpcCatalog;"),
                (lookup, original),
            };
            foreach (var (after, before) in replacements)
            {
                RequireOnce(after);
                text = text.Replace(after, before, StringComparison.Ordinal);
            }
        }
        if (Path.GetFileName(path) == "McpManagementRpcProvider.cs")
        {
            var legacy = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "McpManagementRpcProvider.Legacy.cs"));
            const string marker = "internal sealed class McpLocalManagementGateway";
            RequireOnce(marker);
            var provider = legacy[legacy.IndexOf("internal sealed class McpManagementRpcProvider", StringComparison.Ordinal)..].TrimEnd();
            text = text.Replace("using System.Threading.Channels;", "using System.Threading.Channels;\nusing Sunder.Package.Agent.Mcp.Protocol.Generated;", StringComparison.Ordinal)
                .Replace(marker, provider + "\n\n" + marker, StringComparison.Ordinal);
        }
        if (Path.GetFileName(path) == "McpAppRpcGateway.cs")
        {
            var legacy = ReadMigrationSourceText(Path.Combine(Path.GetDirectoryName(path)!, "McpOwnerManagementRpcClient.Legacy.cs"));
            const string marker = "internal sealed class McpAppRuntimeGateway";
            RequireOnce(marker);
            var client = legacy[legacy.IndexOf("internal sealed class McpOwnerManagementRpcClient", StringComparison.Ordinal)..].TrimEnd();
            text = "using System.Runtime.CompilerServices;\n" + text.Replace("using Microsoft.Extensions.Logging;", "using Microsoft.Extensions.Logging;\nusing Sunder.Package.Agent.Mcp.Protocol.Generated;", StringComparison.Ordinal)
                .Replace(marker, client + "\n\n" + marker, StringComparison.Ordinal)
                .Replace("McpOAuthConstants.HandlerId", "McpManagementProtocol.OAuthCallbackHandlerId", StringComparison.Ordinal)
                .Replace("McpOAuthConstants.ServerIdParameter", "McpManagementProtocol.OAuthServerIdParameter", StringComparison.Ordinal);
        }
        if (Path.GetFileName(path) == "McpOAuthCallbackHandler.cs")
            text = text.Replace("McpOAuthConstants.HandlerId", "McpManagementProtocol.OAuthCallbackHandlerId", StringComparison.Ordinal)
                .Replace("McpOAuthConstants.ServerIdParameter", "McpManagementProtocol.OAuthServerIdParameter", StringComparison.Ordinal);
        if (Path.GetFileName(path) == "McpManagementProtocol.cs")
            text = text.Replace("Services.McpOAuthConstants.HandlerId", "\"mcp.oauth.v1\"", StringComparison.Ordinal)
                .Replace("Services.McpOAuthConstants.ServerIdParameter", "\"serverId\"", StringComparison.Ordinal);
        return text;

        void RequireOnce(string marker)
        {
            var count = text.Split(marker, StringSplitOptions.None).Length - 1;
            Assert.True(count == 1, $"{path}: expected one pre-natural MCP occurrence of '{marker}', found {count}.");
        }
    }
}
