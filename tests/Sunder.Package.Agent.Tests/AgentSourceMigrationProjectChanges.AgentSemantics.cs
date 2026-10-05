using Xunit;

namespace Sunder.Package.Agent.Tests;

internal static partial class AgentSourceMigrationProjectChanges
{
    internal static readonly string[] AgentSemanticHelpers = new[]
    {
        "AgentRpcContractIds.cs", "AgentRunControlRegistry.cs", "AgentChatWireMapper.cs", "AgentChatFailureProjection.cs",
    }.Select(file => "src/Sunder.Package.Agent/Rpc/Consumers/" + file).ToArray();

    internal static string BeforeAgentSemanticSeparation(string path, string text)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.EndsWith("/src/Sunder.Package.Agent.Contracts/Rpc/AgentRpcSimpleBindings.cs", StringComparison.Ordinal))
            return InsertBefore(text, "public static class AgentRpcServices", Body("AgentRpcContractIds.cs", "public static class AgentRpcContractIds"));
        if (normalized.EndsWith("/src/Sunder.Package.Agent.Contracts/Rpc/AgentBehaviorLoopRpcBinding.cs", StringComparison.Ordinal))
            return InsertBefore(text, "public static class AgentBehaviorLoopRpc", Body("AgentRunControlRegistry.cs", "public sealed class AgentRunControlRegistry"));
        if (normalized.EndsWith("/src/Sunder.Package.Agent/Services/AgentFailurePresentationPolicy.cs", StringComparison.Ordinal))
            return ReplaceOnce(text, "AgentChatFailureProjection.StreamQueueLimitErrorCode", "AgentChatProviderRpc.StreamQueueLimitErrorCode");
        if (!normalized.EndsWith("/src/Sunder.Package.Agent.Contracts/Rpc/AgentChatProviderRpcBinding.cs", StringComparison.Ordinal)) return text;

        var errors = Body("AgentChatFailureProjection.cs", "    internal static SunderRpcException? CreateProjectedRpcException");
        Assert.EndsWith("}\n", errors, StringComparison.Ordinal);
        errors = errors[..^2].TrimEnd();
        const string projected = "    internal static SunderRpcException? CreateProjectedRpcException(AgentChatErrorEvent error)\n        => AgentChatFailureProjection.CreateProjectedRpcException(error);\n\n"
            + "    internal static AgentChatProviderException CreateProviderException(AgentChatErrorEvent error)\n        => AgentChatFailureProjection.CreateProviderException(error);";
        text = ReplaceOnce(text, projected, errors);
        text = ReplaceOnce(text, "= AgentChatFailureProjection.StreamQueueLimitErrorCode;", "= \"rpc.worker.stream-queue-limit\";");
        text = ReplaceOnce(text, "= AgentChatFailureProjection.UnsafeRpcFailureErrorCode;", "= \"rpc.worker.unsafe-dispatch\";");
        var mapper = Body("AgentChatWireMapper.cs", "internal static class AgentChatWireMapper");
        const string timestamps = "\n    private static SunderRpcOffsetDateTime ToWireTimestamp(DateTimeOffset value)";
        Assert.Equal(2, mapper.Split(timestamps, StringSplitOptions.None).Length);
        mapper = mapper[..mapper.IndexOf(timestamps, StringComparison.Ordinal)] + "}\n";
        mapper = ReplaceOnce(mapper, "ToWireTimestamp(createdAt)", "AgentRpcServiceHandler.ToWireTimestamp(createdAt)");
        mapper = ReplaceOnce(mapper, "FromWireTimestamp(createdAt)", "AgentRpcServiceHandler.FromWireTimestamp(createdAt)");
        Assert.DoesNotContain("internal static class AgentChatWireMapper", text, StringComparison.Ordinal);
        return text.TrimEnd() + "\n\n" + mapper;

        static string Body(string file, string marker)
        {
            var source = File.ReadAllText(Path.Combine(AgentPackageRepositoryInventory.RepositoryRoot.FullName,
                "src/Sunder.Package.Agent/Rpc/Consumers", file)).Replace("\r\n", "\n", StringComparison.Ordinal);
            Assert.Equal(2, source.Split(marker, StringSplitOptions.None).Length);
            return source[source.IndexOf(marker, StringComparison.Ordinal)..];
        }
        static string InsertBefore(string source, string marker, string body)
            => ReplaceOnce(source, marker, body.TrimEnd() + "\n\n" + marker);
        static string ReplaceOnce(string source, string current, string previous)
        {
            Assert.Equal(2, source.Split(current, StringSplitOptions.None).Length);
            return source.Replace(current, previous, StringComparison.Ordinal);
        }
    }
}
